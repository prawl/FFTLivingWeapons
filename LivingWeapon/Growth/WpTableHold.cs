using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Turn-scoped write to the resident item-stats WP byte for the "wp"/"wp+faith" guns (the three
/// plain and three magic guns that Routes gives no combat-struct lane, see GrowthEngine.Lanes.cs). While the
/// acting unit is a player wielding one at tier &gt; 0, holds the byte (Offsets.ItemStatsBase) at
/// bakedWP + Tuning.WpBonus[tier] and restores the baked value as soon as that stops being true.
/// Follows WeaponPalette's desire/restore split with the fail-safe inverted (N1). WeaponPalette holds
/// on a FlagOwner refusal, but a stale WP bump is live damage for every wielder of that id, enemies
/// included, so refusal here leans toward restore. A short gap is tolerated (<see cref="Tuning.WpUnresolvedRestoreTicks"/>
/// unresolved ticks, like Tuning.BulwarkUnresolvedTicks) so a band-scan gap between turns does not drop a
/// healthy hold. Clean signals (enemy turn, other weapon, tier 0, battle exit, ResetBattle) restore at once.
/// Accepted. An enemy reaction shot with the same gun id inside the wielder's turn sees the bump too.
/// It is rare, bounded by the cadence-1 restore, and this table cannot tell who fires.
/// </summary>
internal sealed class WpTableHold
{
    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;

    private long _addr = -1;      // -1 = nothing outstanding
    private int _weaponId = -1;
    private byte _baked;
    private byte _target;         // the last value WE actually wrote (== _baked until the first write)
    private int _unresolvedTicks;

    // Ids stood down for the rest of this battle after a foreign-writer read (as WeaponPalette's
    // _suspectWarned). A byte matching neither the baked WP nor our target belongs to someone else.
    private readonly HashSet<int> _standDown = new();

    // Extended ids (261+) have no row in the resident stats table; their 8-byte
    // row is in the mod's weapon-stat stub page. Returns that address, or -1 if not an armed extended
    // item. Null (tests, no extended inventory) refuses such ids.
    private readonly Func<int, long>? _extendedRowAddr;
    private readonly DoorLatch? _latch;

    internal DoorLatch? Latch => _latch;

    public WpTableHold(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory mem,
        Func<int, long>? extendedRowAddr = null, DoorLatch? latch = null)
    {
        _latch = latch;
        _meta = meta;
        _kills = kills;
        _mem = mem;
        _extendedRowAddr = extendedRowAddr;
    }

    public void Tick(bool inLive)
    {
        if (!inLive) { Restore(); return; }   // a genuine battle-exit frame. No grace

        if (!Band.FlagOwner(_mem, out long entry, out _))
        {
            if (++_unresolvedTicks >= Tuning.WpUnresolvedRestoreTicks) Restore();
            return;
        }
        _unresolvedTicks = 0;

        int weaponId = _mem.U16(entry + Offsets.AWeapon);
        if (!Wants(entry, weaponId, out int tier)) { Restore(); return; }   // enemy turn / weapon change / tier 0

        long addr = StatsAddr(weaponId);
        if (addr < 0) { Restore(); return; }   // an extended id with no armed row. Nothing to bump
        if (_addr != -1 && _addr != addr) Restore();   // the wielder swapped to a different wp weapon
        if (_standDown.Contains(weaponId)) return;      // a foreign writer already flagged this id this battle

        Apply(addr, weaponId, tier);
    }

    /// <summary>Uses WeaponPalette.Desire's player filter (Wielder.ResolveAnyHandNameId, matchCount &gt;= 1).
    /// A roster row must wield <paramref name="weaponId"/> at this entry's fingerprint, else it is an enemy
    /// sharing the gun id.</summary>
    private bool Wants(long entry, int weaponId, out int tier)
    {
        tier = 0;
        var fp = ((int)_mem.U8(entry + Offsets.ALevel), (int)_mem.U8(entry + Offsets.ABrave), (int)_mem.U8(entry + Offsets.AFaith));
        Wielder.ResolveAnyHandNameId(_mem, weaponId, fp, out int matchCount);
        if (matchCount == 0) return false;   // no roster row wields it at this fingerprint, so an enemy
        // Indexing past ItemStatsRows would write into the EquipBonus table behind it, so
        // extended ids are served only through the extended-row resolver.
        if (weaponId >= Offsets.ItemStatsRows && _extendedRowAddr == null) return false;
        if (!_meta.TryGetValue(weaponId, out var m) || (m.Lane != "wp" && m.Lane != "wp+faith")) return false;
        tier = Tuning.TierOf(_kills, weaponId);
        return tier > 0;
    }

    /// <summary>The WP byte to hold. The resident table row for a vanilla-range id; the mod's own
    /// stub row (same 8-byte layout, Power at +4) for an armed extended id; -1 for an extended id
    /// the resolver does not know.</summary>
    private long StatsAddr(int weaponId)
    {
        if (weaponId < Offsets.ItemStatsRows)
            return Offsets.ItemStatsBase + (long)weaponId * Offsets.ItemStatsStride + Offsets.ItemStatsWpOff;
        long row = _extendedRowAddr?.Invoke(weaponId) ?? -1;
        return row < 0 ? -1 : row + Offsets.ItemStatsWpOff;
    }

    /// <summary>Before a write the byte must read the baked WP or our last target (N1). Anything else is
    /// a foreign write, logged once and stood down for the battle. If the foreign value appears while
    /// a hold is outstanding at this address (<paramref name="addr"/> == <see cref="_addr"/>), the writer
    /// already overwrote our bump, so the record is cleared with no write rather than left for Restore to
    /// stomp back to baked. With no hold yet at this address it just refuses to write.</summary>
    private void Apply(long addr, int weaponId, int tier)
    {
        if (weaponId < Offsets.ItemStatsRows && _latch is { } door && !door.Allows()) return;
        if (!_mem.Writable(addr, 1)) return;
        if (!_meta.TryGetValue(weaponId, out var m)) return;
        byte baked = (byte)m.Wp;
        byte newTarget = (byte)(m.Wp + Tuning.WpBonus[tier]);
        byte cur = _mem.U8(addr);

        bool continuing = _addr == addr;
        byte expectedHeld = continuing ? _target : baked;   // what WE expect the byte to currently read
        bool ownedByte = cur == baked || cur == expectedHeld;
        if (!ownedByte)
        {
            if (_standDown.Add(weaponId))
                ModLogger.Warn(LogVerb.Growth, $"{LogNames.Weapon(weaponId)}'s resident WP byte reads {cur}, matching neither the baked value {baked} nor our own hold; standing this weapon's WP bump down for the rest of the battle.");
            if (continuing) { _addr = -1; _weaponId = -1; _baked = 0; _target = 0; }
            return;
        }

        if (cur != newTarget) _mem.W8(addr, newTarget);
        _addr = addr; _weaponId = weaponId; _baked = baked; _target = newTarget;
    }

    /// <summary>Restores with a read-back check. The resident item-stats table is not refreshed on battle
    /// load (unlike WeaponPalette's banks), so every path that ends the window writes the baked value back.</summary>
    private void Restore()
    {
        if (_addr == -1) return;
        if (_mem.Writable(_addr, 1))
        {
            _mem.W8(_addr, _baked);
            bool ok = _mem.U8(_addr) == _baked;
            ModLogger.Debug(LogVerb.Growth, $"wp-table: restored {LogNames.Weapon(_weaponId)}'s WP to {_baked} ({(ok ? "SET" : "MISS")})");
        }
        _addr = -1; _weaponId = -1; _baked = 0; _target = 0;
    }

    public void ResetBattle()
    {
        Restore();
        _unresolvedTicks = 0;
        _standDown.Clear();
    }
}
