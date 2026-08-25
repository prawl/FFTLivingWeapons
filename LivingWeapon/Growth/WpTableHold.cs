using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-317: the turn-scoped resident item-stats WP write for the "wp"/"wp+faith" gun lanes
/// (the three plain guns and three magic guns Routes contributes NO combat-struct lane for --
/// see GrowthEngine.Lanes.cs's Routes, "wp" case). While the acting unit is a PLAYER wielding a
/// wp/wp+faith gun at tier &gt; 0, holds the resident stats-table WP byte (Offsets.ItemStatsBase)
/// at bakedWP + Tuning.WpBonus[tier]; restores the baked value the instant that stops being true.
///
/// Shape mirrors WeaponPalette's desire/restore split (Band.FlagOwner acting-unit resolve, a
/// desired-vs-painted decision, a restore path) with the FAIL-SAFE INVERTED (N1): WeaponPalette
/// HOLDS on a FlagOwner refusal (a stale colour beats a wrong one), but a stale WP bump is LIVE
/// DAMAGE for every same-id wielder including enemies, so refusal here leans toward RESTORE.
/// A short refusal is still tolerated (the restore lands once <see cref="Tuning.WpUnresolvedRestoreTicks"/> consecutive unresolved ticks accrue) -- a
/// transient band-scan gap between turns must not yank a healthy hold mid-turn, mirroring
/// Tuning.BulwarkUnresolvedTicks / Tuning.ProvokeMarkedMissTicks' own "one miss is not a
/// pattern" precedent -- but any CLEAN signal (an enemy's turn, a different weapon, tier 0, a
/// genuine battle exit, or ResetBattle) restores immediately, no grace.
///
/// Accepted blast radius (documented, not a bug): an enemy REACTION shot with the same gun id,
/// landing inside the wielder's own turn window, sees the bump too -- rare, bounded by the
/// cadence-1 restore, and cheaper to accept than adding a same-shot attribution check this
/// table cannot make (the byte has no idea WHO is about to fire it).
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

    // Ids stood down for the rest of THIS battle after a foreign-writer read (the WeaponPalette
    // suspect-check precedent, WeaponPalette.cs's _suspectWarned): a byte reading neither the
    // baked WP nor our own held target belongs to someone else, and re-fighting it every tick is
    // worse than one loud warning and standing clear.
    private readonly HashSet<int> _standDown = new();

    public WpTableHold(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory mem)
    {
        _meta = meta;
        _kills = kills;
        _mem = mem;
    }

    public void Tick(bool inLive)
    {
        if (!inLive) { Restore(); return; }   // a genuine battle-exit frame: no grace

        if (!Band.FlagOwner(_mem, out long entry, out _))
        {
            if (++_unresolvedTicks >= Tuning.WpUnresolvedRestoreTicks) Restore();
            return;
        }
        _unresolvedTicks = 0;

        int weaponId = _mem.U16(entry + Offsets.AWeapon);
        if (!Wants(entry, weaponId, out int tier)) { Restore(); return; }   // enemy turn / weapon change / tier 0

        long addr = StatsAddr(weaponId);
        if (_addr != -1 && _addr != addr) Restore();   // the wielder swapped to a different wp weapon
        if (_standDown.Contains(weaponId)) return;      // a foreign writer already flagged this id this battle

        Apply(addr, weaponId, tier);
    }

    /// <summary>Resolve the acting PLAYER unit via the WeaponPalette.Desire player filter
    /// (Wielder.ResolveAnyHandNameId, matchCount &gt;= 1): a roster row must actually wield
    /// <paramref name="weaponId"/> at the acting entry's own fingerprint, or this is an enemy
    /// sharing the same catalogued gun id, never a bump candidate.</summary>
    private bool Wants(long entry, int weaponId, out int tier)
    {
        tier = 0;
        var fp = ((int)_mem.U8(entry + Offsets.ALevel), (int)_mem.U8(entry + Offsets.ABrave), (int)_mem.U8(entry + Offsets.AFaith));
        Wielder.ResolveAnyHandNameId(_mem, weaponId, fp, out int matchCount);
        if (matchCount == 0) return false;   // no roster row wields it at this fingerprint -- an enemy
        if (!_meta.TryGetValue(weaponId, out var m) || (m.Lane != "wp" && m.Lane != "wp+faith")) return false;
        tier = Tuning.TierOf(_kills, weaponId);
        return tier > 0;
    }

    private static long StatsAddr(int weaponId)
        => Offsets.ItemStatsBase + (long)weaponId * Offsets.ItemStatsStride + Offsets.ItemStatsWpOff;

    /// <summary>Write discipline (N1): before ANY write, the byte must read either the baked WP
    /// or the target WE last held it at -- anything else is a foreign write this hold must never
    /// stomp, logged once and stood down for the battle (the foreign-writer guard, the
    /// WeaponPalette suspect-check pattern). Rev 3 ruling R3-6: when the foreign value is found
    /// WHILE a hold is outstanding at this address (<paramref name="addr"/> == <see cref="_addr"/>),
    /// the foreign writer has already overwritten our bump -- nothing of ours is outstanding
    /// anymore, so the record is CLEARED with no write, rather than left standing for a later
    /// Restore() to stomp the foreign value back to baked. The pre-hold foreign case (no record
    /// yet at this address) has nothing to clear and keeps its existing refuse-to-write
    /// behavior.</summary>
    private void Apply(long addr, int weaponId, int tier)
    {
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

    /// <summary>Physical restore with a read-back verify -- the resident item-stats table gets NO
    /// battle-load refresh (unlike WeaponPalette's banks, which a fresh battle load re-populates
    /// from the loaded file), so every path that ends this hold's window must write the baked
    /// value back for real.</summary>
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
