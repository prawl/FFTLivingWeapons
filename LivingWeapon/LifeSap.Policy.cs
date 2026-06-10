using System;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Umbral Rod's "Life Sap" signature -- no memory access.
/// The stateful kill-diff watcher and the wielder locate live in LifeSap.cs.
/// </summary>
internal sealed partial class LifeSap
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || !sig.LifeSapOnKill) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>The kill-tally-diff trigger (the ExtraTurn freshKill pattern): fires only when a
    /// PRIMED count climbs. -1 = unprimed (first sight after a reset baselines silently, so a
    /// seeded tally or a battle re-entry can never fire a phantom heal).</summary>
    public static bool FreshKill(int lastCount, int count) => lastCount >= 0 && count > lastCount;

    /// <summary>The heal: round(maxHp * pct) away from zero, floor 1 for any positive maxHp
    /// (a sub-1 rounding would silently dead the grant on tiny units). 0 when maxHp is junk.</summary>
    public static int HealAmount(int maxHp, double pct)
    {
        if (maxHp <= 0) return 0;
        int heal = (int)Math.Round(maxHp * pct, MidpointRounding.AwayFromZero);
        return heal < 1 ? 1 : heal;
    }

    /// <summary>New HP after the heal: clamped at maxHp, and a dead wielder (hp &lt;= 0) is left
    /// alone -- a kill heal must NEVER revive (HP 0 -&gt; positive is the engine's revival signal).</summary>
    public static int NewHp(int hp, int maxHp, int heal)
    {
        if (hp <= 0 || maxHp <= 0) return hp;
        int n = hp + heal;
        return n > maxHp ? maxHp : n;
    }

    /// <summary>Guarded little-endian u16 write of the wielder's HP on its band entry
    /// (the authoritative copy -- the same field Ricochet's chip writes). Fail-safe no-op
    /// when the page isn't writable.</summary>
    public static void WriteHp(long entryAddr, int newHp)
    {
        long a = entryAddr + Offsets.AHp;
        if (!Mem.Writable(a, 2)) return;
        Mem.W8(a, (byte)(newHp & 0xFF));
        Mem.W8(a + 1, (byte)((newHp >> 8) & 0xFF));
    }
}
