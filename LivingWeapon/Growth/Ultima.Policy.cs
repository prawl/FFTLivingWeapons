namespace LivingWeapon;

/// <summary>
/// The pure HP%-scaling logic behind the Materia Blade's "Ultima" signature -- no live state,
/// no memory access. The stateful hold (the PA write against the engine's per-turn normalize)
/// lives in GrowthEngine (HoldUltima), exactly as HoldAfterimage does for Swiftedge.
///
/// Faithful to FF7's Ultima Weapon: the wielder's damage scales with their current HP%.
/// At full health the PA multiplier is at its peak; every hit chips away at their output.
/// The kill tier only RAISES the whole curve (Tuning.UltimaMul), so a +3 blade is never
/// a death trap -- a hurt wielder is weaker than a fresh one at the same tier, but always
/// stronger than a fresh wielder at a lower tier.
/// </summary>
internal static class UltimaPolicy
{
    /// <summary>HP% band index from current and max HP.
    /// 0 = 100%+ (full or buffed above max), 1 = 75-99%, 2 = 50-74%, 3 = 25-49%, 4 = &lt;25%.
    /// -1 = unreadable / dead (caller must leave PA at natural -- NO scaling applied).
    /// Integer math only (no float): boundaries hp>=maxHp, hp*4>=maxHp*3, hp*2>=maxHp, hp*4>=maxHp.</summary>
    public static int Band(int hp, int maxHp)
    {
        if (maxHp <= 0 || hp <= 0) return -1;
        if (hp >= maxHp)            return 0;   // 100%+ (buffed HP counts as full)
        if (hp * 4 >= maxHp * 3)   return 1;   // 75-99%
        if (hp * 2 >= maxHp)       return 2;   // 50-74%
        if (hp * 4 >= maxHp)       return 3;   // 25-49%
        return 4;                               // <25%
    }

    /// <summary>The held PA for the given natural PA, current HP, and kill tier.
    /// Unreadable HP (band &lt; 0) returns naturalPa unchanged -- the critical safety case that
    /// prevents nuking PA to the &lt;25% multiplier when the band struct can't be matched.
    /// Rounding is MidpointRounding.AwayFromZero (13.8->14, never banker's rounding) so the
    /// table is predictable in tests. This deliberately diverges from GrowthEngine.WriteTarget
    /// (default banker's rounding on the other stat lanes) -- Ultima owns the PA lane via
    /// OwnsPa (Route declines it), so the two rounding paths never touch the same byte.
    /// Clamp ([1,255]) is NOT applied here; HoldUltima applies GrowthEngine.Clamp after the call.</summary>
    public static int PaHeld(int naturalPa, int hp, int maxHp, int tier, int[][] table)
    {
        int band = Band(hp, maxHp);
        if (band < 0) return naturalPa;   // safety: no HP reading -> don't touch PA
        int pct = table[tier][band];
        return (int)System.Math.Round(naturalPa * pct / 100.0, System.MidpointRounding.AwayFromZero);
    }

    /// <summary>True when the weapon's signature flags Ultima (the Materia Blade's always-on lane).</summary>
    public static bool IsUltima(WeaponSignature? sig) => sig is { Ultima: true };
}
