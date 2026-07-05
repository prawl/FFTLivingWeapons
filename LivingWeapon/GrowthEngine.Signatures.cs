using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The signature half of GrowthEngine: the iconic ability/stat a weapon GRANTS at its kill-tier,
/// held on the combat struct alongside the stat growth. Three flavors:
///   - support bit (always-on or HP-gated) -- OR-in at +0x98 (Signatures.ResolveSupport/ConditionMet);
///   - timed flat stat (first-N-turns) -- write + revert a Speed bonus, gated by TurnTracker.
/// Every write is VirtualQuery-guarded (Mem.Writable) and only ever OR-sets a bit or writes a
/// captured-natural-derived value, so a wrong guess can never corrupt a stat (worst case a buff
/// lingers one battle, which the fresh per-battle combat struct clears).
/// </summary>
internal sealed partial class GrowthEngine
{
    // Grants announced this battle (weapon id), so the read-back log fires once per arm, not every tick.
    private readonly HashSet<int> _grantLogged = new();

    // Support bits held this battle, so a mid-battle unequip (Rend/Steal mutate the roster live)
    // releases the grant instead of letting it linger to battle end: (roster slot, weapon) ->
    // (struct base, bit, ability id, the latch-time brave/faith for the same-unit check).
    private readonly Dictionary<(int slot, int weapon), (long s, int off, byte mask, int abilityId, int brave, int faith)>
        _heldSupports = new();

    /// <summary>Hold this weapon's signature support passive on the combat struct, once its
    /// kill-tier is earned -- OR-in the bit each tick to beat the engine's per-turn normalize,
    /// exactly as the stat hold does. Guarded write; the hold itself never clears (kills only
    /// climb, and the struct is rebuilt fresh each battle) -- ReleaseUnequipped strips the bit
    /// if the weapon leaves the wielder's hands mid-battle.
    /// pickedSupport: the player's chosen roster support id (Offsets.RSupport); used to emit the
    /// redundancy note when the wielder already has the same support equipped (default 0 = unknown).</summary>
    private void HoldSignature(long s, int rosterSlot, int weapon, string name, WeaponSignature? sig, int tier,
                                int hp, int maxHp, int brave, int faith, int pickedSupport = 0)
    {
        if (!Signatures.ResolveSupport(sig, tier, out int off, out byte mask)) return;
        if (!Signatures.ConditionMet(sig, hp, maxHp)) return;   // HP-gate; no-op for always-on signatures
        long addr = s + Offsets.CSupport + off;
        // Latch + announce only a CONFIRMED set: OrSet's read-back fails on an unwritable page
        // AND on the teardown race where the write doesn't stick -- either way, no latch, no
        // log, retry next tick (so the once-per-battle grant log is never burned on a miss).
        if (!MemBits.OrSet(addr, mask, out _)) return;
        _heldSupports[(rosterSlot, weapon)] = (s, off, mask, sig!.AbilityId, brave, faith);
        LogGrantOnce(weapon, name, sig!, off, mask, addr, pickedSupport);
    }

    /// <summary>Mid-battle unequip release (the grant-verification half the OR-only hold lacks):
    /// when a latched weapon no longer sits in its wielder's roster hands, AND-clear the granted
    /// support bit -- unless it is the player's own picked support (never strip their choice), and
    /// only while the latched struct still fingerprint-matches (units migrate between fixed slots;
    /// the Maim.Drive discipline). An unreadable/emptied roster slot keeps the latch (transient or
    /// battle teardown -- the fresh per-battle struct clears it anyway).</summary>
    private void ReleaseUnequipped()
    {
        if (_heldSupports.Count == 0) return;
        List<(int slot, int weapon)>? drop = null;
        foreach (var ((slot, weapon), v) in _heldSupports)
        {
            long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
            if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
            int level = _mem.U8(rb + Offsets.RLevel);
            if (level < 1 || level > 99) continue;
            // A grant is only ever held for a main-hand weapon; check main-hand only for release.
            bool wielded = _mem.U16(rb + Offsets.RRHand) == weapon;
            if (!Signatures.ShouldClearOnUnequip(wielded, _mem.U8(rb + Offsets.RSupport), v.abilityId))
            {
                if (!wielded) (drop ??= new()).Add((slot, weapon));   // unequipped, but the pick is the player's own
                continue;
            }
            (drop ??= new()).Add((slot, weapon));
            if (_mem.U8(v.s + Offsets.CBrave) != v.brave || _mem.U8(v.s + Offsets.CFaith) != v.faith) continue;
            if (MemBits.Clear(v.s + Offsets.CSupport + v.off, v.mask))
                ModLogger.EventWithTrace(LogVerb.Grant,
                    $"{LogNames.Weapon(weapon)} was unequipped; its granted ability is switched off.",
                    $"grant released (party slot {slot}, ability {v.abilityId})");
        }
        if (drop is not null)
            foreach (var k in drop) { _heldSupports.Remove(k); _grantLogged.Remove(k.weapon); }   // re-equip re-announces
    }

    /// <summary>Read the granted bit back and announce it once per weapon per battle: confirms the
    /// write landed (SET vs MISS) and decodes the ability by name -- the clean test signal that
    /// replaces eyeballing a memory diff. Warns when the support is build-time-only (e.g. HP Boost),
    /// whose live bit can't take effect, so a dud signature is obvious in the log. When the player's
    /// picked support matches the grant, emits a redundancy note (same bit -> no stack).</summary>
    private void LogGrantOnce(int weapon, string name, WeaponSignature sig, int off, byte mask, long addr,
                               int pickedSupport = 0)
    {
        if (!_grantLogged.Add(weapon)) return;
        bool present = (_mem.U8(addr) & mask) != 0;   // read-back: did our write actually land?
        ModLogger.EventWithTrace(LogVerb.Grant,
            $"{name} bestows {sig.DisplayLabel} on its wielder.",
            $"grant detail (support ability {sig.AbilityId}, readback={(present ? "SET" : "MISS")}, +0x98[{off}]=0x{mask:X2})");
        if (!present)
            ModLogger.Warn(LogVerb.Grant, $"The {sig.DisplayLabel} grant could not be confirmed by read-back; it may not take effect.");
        else if (Signatures.IsBuildTimeOnly(sig.AbilityId))
            ModLogger.Warn(LogVerb.Grant, $"{sig.DisplayLabel} is a build-time-only support; the live grant will not take effect.");
        if (pickedSupport != 0 && pickedSupport == sig.AbilityId)
            ModLogger.Event(LogVerb.Grant, $"The wielder already chose {sig.DisplayLabel} as their support; the weapon's grant adds nothing (pick a different support to benefit).");
    }

    /// <summary>Read a unit's (currentHP, maxHP) from the BAND by its (level,brave,faith)
    /// fingerprint -- the combat struct doesn't carry HP, and the static array freezes on
    /// battle restart (stale HP breaks the HP-gated guard after a restart). Returns (0,0)
    /// if no band slot matches. Only called for conditional (HP-gated) signatures.
    /// Prefers real-position entries over (0,0) twins.
    /// TWO-TIER (D7): tier 1 (rosterNameId &gt; 0) requires an exact band-entry nameId match;
    /// a miss falls back to tier 2 (today's plain fingerprint scan, veto-hardened -- an entry
    /// whose nameId reads NONZERO and differs from rosterNameId is excluded). Per D8, the
    /// nameId read is UNGUARDED (the shipped Iai/Wielder pattern): Mem's U16 fail-safes to 0 on
    /// an unreadable address, and a guarded Readable pre-filter would make tier 1 permanently
    /// dead against FakeSparseMemory's allowlist-true-only fake in every test.</summary>
    internal static (int hp, int maxHp) ReadHp(IGameMemory mem, int level, int brave, int faith, int rosterNameId = 0)
    {
        if (rosterNameId > 0)
        {
            var (found1, hp1, maxHp1) = ReadHpScan(mem, level, brave, faith, rosterNameId, exact: true);
            if (found1) return (hp1, maxHp1);
        }
        var (_, hp2, maxHp2) = ReadHpScan(mem, level, brave, faith, rosterNameId, exact: false);
        return (hp2, maxHp2);
    }

    /// <summary>One full band pass under either mode: <paramref name="exact"/> = tier 1 (requires
    /// the band entry's nameId == rosterNameId, unguarded read; only called with rosterNameId
    /// &gt; 0); !exact = tier 2 (today's fingerprint match, plus the D2 veto when rosterNameId
    /// &gt; 0 -- a foreign nonzero nameId excludes the entry, 0/unseeded passes).</summary>
    private static (bool found, int hp, int maxHp) ReadHpScan(IGameMemory mem, int level, int brave, int faith,
                                                              int rosterNameId, bool exact)
    {
        (int hp, int maxHp) result = (0, 0);
        bool foundReal = false, found = false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Offsets.BandReadBase + (long)s * Offsets.CombatStride;
            if (!mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            if (!Band.LevelMatchesRoster(level, mem.U8(addr + Offsets.ALevel))) continue;   // level = pre-battle roster value
            if (mem.U8(addr + Offsets.ABrave) != brave) continue;
            if (mem.U8(addr + Offsets.AFaith) != faith) continue;
            if (exact)
            {
                int entryNameId = mem.U16(addr + Offsets.ANameId);   // unguarded fail-safe read (Iai pattern, D8)
                if (entryNameId != rosterNameId) continue;
            }
            else if (rosterNameId > 0)
            {
                int entryNameId = mem.U16(addr + Offsets.ANameId);
                if (entryNameId != 0 && entryNameId != rosterNameId) continue;   // D2 veto
            }
            bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
            if (foundReal && !realPos) continue;   // prefer real over twin
            result = (mem.U16(addr + Offsets.AHp), mem.U16(addr + Offsets.AMaxHp));
            found = true;
            if (realPos) foundReal = true;
        }
        return (found, result.hp, result.maxHp);
    }

    /// <summary>Hold a TIMED flat stat bonus (Galewind's Speed +3 for the wielder's first ForTurns
    /// turns), then revert. Captures natural on first sight while active and re-applies natural+bonus
    /// against the per-turn normalize; once the window passes, restores the captured natural and stops
    /// tracking. Only ever writes natural or natural+bonus (both guarded) -- worst case a buff lingers
    /// a turn (and resets next battle), never a corrupt value. Speed is the only wired stat today.</summary>
    internal void HoldTimedStat(long s, WeaponSignature sig, int tier, int turns)
    {
        if (tier < sig.AtTier || sig.StatBonus == 0 || sig.Stat != "Speed") return;
        long addr = s + Offsets.CSpeed;
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        bool active = sig.Mounted
            ? (_mem.U8(s + Offsets.CMount) & Offsets.CMountRidingBit) != 0   // riding a chocobo
            : turns < sig.ForTurns;
        if (_timedNatural.TryGetValue(addr, out int nat))
        {
            int boosted = Clamp(nat + sig.StatBonus);
            if (active) { if (cur == nat) _mem.W8(addr, (byte)boosted); }   // re-apply after a normalize
            else
            {
                if (cur == boosted) _mem.W8(addr, (byte)nat);   // window over -> revert our boost
                _timedNatural.Remove(addr);
            }
        }
        else if (active && cur >= StatMin && cur <= StatSaneHi)   // first sight while active: capture + apply
        {
            _timedNatural[addr] = cur;
            _mem.W8(addr, (byte)Clamp(cur + sig.StatBonus));
        }
    }

    private static int Clamp(int v) => v < StatMin ? StatMin : v > StatMax ? StatMax : v;
}
