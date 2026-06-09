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

    /// <summary>Hold this weapon's signature support passive on the combat struct, once its
    /// kill-tier is earned -- OR-in the bit each tick to beat the engine's per-turn normalize,
    /// exactly as the stat hold does. Guarded write; never clears (kills only climb, and the
    /// struct is rebuilt fresh each battle, so it re-arms naturally).
    /// pickedSupport: the player's chosen roster support id (Offsets.RSupport); used to emit the
    /// redundancy note when the wielder already has the same support equipped (default 0 = unknown).</summary>
    private void HoldSignature(long s, int weapon, string name, WeaponSignature? sig, int tier, int hp, int maxHp,
                                int pickedSupport = 0)
    {
        if (!Signatures.ResolveSupport(sig, tier, out int off, out byte mask)) return;
        if (!Signatures.ConditionMet(sig, hp, maxHp)) return;   // HP-gate; no-op for always-on signatures
        long addr = s + Offsets.CSupport + off;
        if (!Mem.Writable(addr, 1)) return;
        int cur = Mem.U8(addr);
        if ((cur & mask) == 0) Mem.W8(addr, (byte)(cur | mask));
        LogGrantOnce(weapon, name, sig!, off, mask, addr, pickedSupport);
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
        bool present = (Mem.U8(addr) & mask) != 0;   // read-back: did our write actually land?
        string warn = Signatures.IsBuildTimeOnly(sig.AbilityId)
            ? "  WARN build-time-only support -- a live bit will NOT take effect"
            : "";
        Log.Info($"GRANT {name} -> {sig.DisplayLabel} (support {sig.AbilityId}) @ +0x98[{off}]=0x{mask:X2} readback={(present ? "SET" : "MISS")}{warn}");
        if (pickedSupport != 0 && pickedSupport == sig.AbilityId)
            Log.Info($"note: wielder already equips {sig.DisplayLabel} -- the weapon grant adds nothing (pick a different support)");
    }

    /// <summary>Read a unit's (currentHP, maxHP) from the BAND by its (level,brave,faith)
    /// fingerprint -- the combat struct doesn't carry HP, and the static array freezes on
    /// battle restart (stale HP breaks the HP-gated guard after a restart). Returns (0,0)
    /// if no band slot matches. Only called for conditional (HP-gated) signatures.
    /// Prefers real-position entries over (0,0) twins; uses static Mem (GrowthEngine pattern).</summary>
    private (int hp, int maxHp) ReadHp(int level, int brave, int faith)
    {
        (int hp, int maxHp) result = (0, 0);
        bool foundReal = false;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Offsets.BandReadBase + (long)s * Offsets.CombatStride;
            if (!Mem.Readable(addr + Offsets.AMaxHp, 2)) continue;
            if (Mem.U8(addr + Offsets.ALevel) != level) continue;
            if (Mem.U8(addr + Offsets.ABrave) != brave) continue;
            if (Mem.U8(addr + Offsets.AFaith) != faith) continue;
            bool realPos = Mem.U8(addr + Offsets.AGx) != 0 || Mem.U8(addr + Offsets.AGy) != 0;
            if (foundReal && !realPos) continue;   // prefer real over twin
            result = (Mem.U16(addr + Offsets.AHp), Mem.U16(addr + Offsets.AMaxHp));
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
        if (!Mem.Writable(addr, 1)) return;
        int cur = Mem.U8(addr);
        bool active = turns < sig.ForTurns;
        if (_timedNatural.TryGetValue(addr, out int nat))
        {
            int boosted = Clamp(nat + sig.StatBonus);
            if (active) { if (cur == nat) Mem.W8(addr, (byte)boosted); }   // re-apply after a normalize
            else
            {
                if (cur == boosted) Mem.W8(addr, (byte)nat);   // window over -> revert our boost
                _timedNatural.Remove(addr);
            }
        }
        else if (active && cur >= StatMin && cur <= StatSaneHi)   // first sight while active: capture + apply
        {
            _timedNatural[addr] = cur;
            Mem.W8(addr, (byte)Clamp(cur + sig.StatBonus));
        }
    }

    private static int Clamp(int v) => v < StatMin ? StatMin : v > StatMax ? StatMax : v;
}
