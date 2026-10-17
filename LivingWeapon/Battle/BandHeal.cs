using System;

namespace LivingWeapon;

/// <summary>Shared band-entry HP heal core. Heal sizing, the clamp-without-revive rule and the guarded
/// HP write. Moved out of LifeSap.Policy.cs in LW-149 stage G because Benediction, Renewal and
/// SpiritualFont borrowed it; LifeSap itself was deleted in LW-159.</summary>
internal static class BandHeal
{
    /// <summary>round(maxHp * pct) away from zero, minimum 1 for a positive maxHp so tiny units still
    /// heal. 0 when maxHp is junk.</summary>
    public static int HealAmount(int maxHp, double pct)
    {
        if (maxHp <= 0) return 0;
        int heal = (int)Math.Round(maxHp * pct, MidpointRounding.AwayFromZero);
        return heal < 1 ? 1 : heal;
    }

    /// <summary>HP after the heal, clamped at maxHp. A dead wielder (hp &lt;= 0) is left alone, since
    /// 0 to positive is the engine's revival signal.</summary>
    public static int NewHp(int hp, int maxHp, int heal)
    {
        if (hp <= 0 || maxHp <= 0) return hp;
        int n = hp + heal;
        return n > maxHp ? maxHp : n;
    }

    /// <summary>Guarded u16 write of the wielder's HP on its band entry (the field Ricochet's chip also
    /// writes). No-op when the page is not writable. One W16 (LW-145 fix 2) because two W8 halves could
    /// expose a torn value when a heal crosses 255 HP.</summary>
    public static void WriteHp(IGameMemory mem, long entryAddr, int newHp)
    {
        long a = entryAddr + Offsets.AHp;
        if (!mem.Writable(a, 2)) return;
        mem.W16(a, (ushort)newHp);
    }
}
