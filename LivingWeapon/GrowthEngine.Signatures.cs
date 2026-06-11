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
                Log.Info($"GRANT released: {LogNames.Weapon(weapon)} was unequipped from party slot {slot} -- {v.abilityId} support bit cleared");
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
        string warn = Signatures.IsBuildTimeOnly(sig.AbilityId)
            ? "  WARN build-time-only support -- a live bit will NOT take effect"
            : "";
        Log.Info($"GRANT {name} -> {sig.DisplayLabel} (support {sig.AbilityId}) @ +0x98[{off}]=0x{mask:X2} readback={(present ? "SET" : "MISS")}{warn}");
        if (pickedSupport != 0 && pickedSupport == sig.AbilityId)
            Log.Info($"note: wielder already has {sig.DisplayLabel} equipped as their chosen support -- the weapon grant adds nothing (pick a different support)");
    }

    /// <summary>Read a unit's (currentHP, maxHP) from the BAND by its (level,brave,faith)
    /// fingerprint -- the combat struct doesn't carry HP, and the static array freezes on
    /// battle restart (stale HP breaks the HP-gated guard after a restart). Returns (0,0)
    /// if no band slot matches. Only called for conditional (HP-gated) signatures.
    /// Prefers real-position entries over (0,0) twins.</summary>
    private (int hp, int maxHp) ReadHp(int level, int brave, int faith)
    {
        (int hp, int maxHp) result = (0, 0);
        bool foundReal = false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Offsets.BandReadBase + (long)s * Offsets.CombatStride;
            if (!_mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            if (!Band.LevelMatchesRoster(level, _mem.U8(addr + Offsets.ALevel))) continue;   // level = pre-battle roster value
            if (_mem.U8(addr + Offsets.ABrave) != brave) continue;
            if (_mem.U8(addr + Offsets.AFaith) != faith) continue;
            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            if (foundReal && !realPos) continue;   // prefer real over twin
            result = (_mem.U16(addr + Offsets.AHp), _mem.U16(addr + Offsets.AMaxHp));
            if (realPos) foundReal = true;
        }
        return result;
    }

    /// <summary>Hold a TIMED flat stat bonus (Galewind's Speed +3 for the wielder's first ForTurns
    /// turns), then revert. Captures natural on first sight while active and re-applies natural+bonus
    /// against the per-turn normalize; once the window passes, restores the captured natural and stops
    /// tracking. Only ever writes natural or natural+bonus (both guarded) -- worst case a buff lingers
    /// a turn (and resets next battle), never a corrupt value. Speed is the only wired stat today.</summary>
    private void HoldTimedStat(long s, WeaponSignature sig, int tier, int turns)
    {
        if (tier < sig.AtTier || sig.StatBonus == 0 || sig.Stat != "Speed") return;
        long addr = s + Offsets.CSpeed;
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        bool active = turns < sig.ForTurns;
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
