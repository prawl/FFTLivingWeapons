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
    /// <summary>Hold this weapon's signature support passive on the combat struct, once its
    /// kill-tier is earned -- OR-in the bit each tick to beat the engine's per-turn normalize,
    /// exactly as the stat hold does. Guarded write; never clears (kills only climb, and the
    /// struct is rebuilt fresh each battle, so it re-arms naturally).</summary>
    private void HoldSignature(long s, WeaponSignature? sig, int tier, int hp, int maxHp)
    {
        if (!Signatures.ResolveSupport(sig, tier, out int off, out byte mask)) return;
        if (!Signatures.ConditionMet(sig, hp, maxHp)) return;   // HP-gate; no-op for always-on signatures
        long addr = s + Offsets.CSupport + off;
        if (!Mem.Writable(addr, 1)) return;
        int cur = Mem.U8(addr);
        if ((cur & mask) == 0) Mem.W8(addr, (byte)(cur | mask));
    }

    /// <summary>Read a unit's (currentHP, maxHP) from the static array by its (level,brave,faith)
    /// fingerprint -- the combat struct doesn't carry HP. Returns (0,0) if no slot matches. Only
    /// called for conditional (HP-gated) signatures, so the always-on path never pays for this walk.</summary>
    private (int hp, int maxHp) ReadHp(int level, int brave, int faith)
    {
        for (int a = 0; a < Offsets.NSlots; a++)
        {
            long slot = Offsets.ArrayReadBase + (long)a * Offsets.ArrayStride;
            if (!Mem.Readable(slot + Offsets.AMaxHp, 2)) continue;
            if (Mem.U8(slot + Offsets.ALevel) != level) continue;
            if (Mem.U8(slot + Offsets.ABrave) != brave) continue;
            if (Mem.U8(slot + Offsets.AFaith) != faith) continue;
            return (Mem.U16(slot + Offsets.AHp), Mem.U16(slot + Offsets.AMaxHp));
        }
        return (0, 0);
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
