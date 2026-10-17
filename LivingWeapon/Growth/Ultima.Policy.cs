namespace LivingWeapon;

/// <summary>
/// Pure HP%-scaling logic for the Materia Blade's "Ultima" signature (FF7 Ultima Weapon): the PA multiplier peaks at full HP and falls as the wielder gets hurt.
/// The kill tier raises the whole curve (Tuning.UltimaMul), so a hurt wielder is weaker than a fresh one at the same tier but stronger than a fresh one at a lower tier.
/// The stateful hold lives in GrowthEngine (HoldUltima), like HoldAfterimage for Swiftedge.
/// </summary>
internal static class UltimaPolicy
{
    /// <summary>HP% band index: 0 = 100%+, 1 = 75-99%, 2 = 50-74%, 3 = 25-49%, 4 = &lt;25%; -1 = unreadable or dead (caller leaves PA at natural).
    /// Integer math only: boundaries hp>=maxHp, hp*4>=maxHp*3, hp*2>=maxHp, hp*4>=maxHp.</summary>
    public static int Band(int hp, int maxHp)
    {
        if (maxHp <= 0 || hp <= 0) return -1;
        if (hp >= maxHp) return 0;   // 100%+ (buffed HP counts as full)
        if (hp * 4 >= maxHp * 3) return 1;   // 75-99%
        if (hp * 2 >= maxHp) return 2;   // 50-74%
        if (hp * 4 >= maxHp) return 3;   // 25-49%
        return 4;                               // <25%
    }

    /// <summary>The held PA for the given natural PA, HP and kill tier. Unreadable HP returns naturalPa unchanged, so a failed band match never drops PA to the &lt;25% multiplier.
    /// Rounds AwayFromZero (13.8->14) so the table is predictable in tests; this differs from GrowthEngine.WriteTarget (banker's) because Ultima owns the PA lane via OwnsPa (Route declines it).
    /// The [1,255] clamp is applied by HoldUltima via GrowthEngine.Clamp.</summary>
    public static int PaHeld(int naturalPa, int hp, int maxHp, int tier, int[][] table)
    {
        int band = Band(hp, maxHp);
        if (band < 0) return naturalPa;   // safety: no HP reading -> don't touch PA
        int pct = table[tier][band];
        return (int)System.Math.Round(naturalPa * pct / 100.0, System.MidpointRounding.AwayFromZero);
    }

    /// <summary>True when the weapon's signature flags Ultima.</summary>
    public static bool IsUltima(WeaponSignature? sig) => sig is { Ultima: true };
}
