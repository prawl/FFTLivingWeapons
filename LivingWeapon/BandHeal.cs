using System;

namespace LivingWeapon;

/// <summary>
/// The shared band-entry HP-heal core: the heal-sizing formula, the clamp-and-never-revive rule,
/// and the guarded little-endian HP write. Promoted out of LifeSap.Policy.cs (LW-149 stage G) --
/// Benediction, Renewal, SpiritualFont, and Wyrmblood each already borrowed LifeSap's HealAmount/
/// NewHp/WriteHp by name for their own heal/regen/aura/font restores, so a neutral home replaces
/// four cross-signature borrows of one signature's name with a shared primitive. LifeSap.Policy
/// keeps thin one-line forwards for all three so its own tests and any missed caller keep working.
/// </summary>
internal static class BandHeal
{
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
    /// when the page isn't writable. One W16 call (LW-145 fix 2): two separate W8 halves opened
    /// a torn-value window a heal crossing 255 HP could transiently expose to the game's own
    /// threads (the low byte written, the high byte not yet).</summary>
    public static void WriteHp(IGameMemory mem, long entryAddr, int newHp)
    {
        long a = entryAddr + Offsets.AHp;
        if (!mem.Writable(a, 2)) return;
        mem.W16(a, (ushort)newHp);
    }
}
