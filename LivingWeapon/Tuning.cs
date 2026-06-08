namespace LivingWeapon;

/// <summary>
/// Living-Weapon growth tuning. Kept in one place so detection, growth, and
/// display agree. Production kill thresholds are 5 / 20 / 50 (escalating: a fast
/// first taste at P, an aspirational max at P3). Temporarily drop KillThresholds
/// to {1,2,3} for fast in-game verification.
/// </summary>
internal static class Tuning
{
    /// <summary>Kills required to reach P / P2 / P3. Production {5,20,50}; set to {1,2,3} to verify fast.</summary>
    public static readonly int[] KillThresholds = { 5, 20, 50 };

    /// <summary>kills -> tier (0..3), checked high to low.</summary>
    public static int TierFor(int kills) =>
        kills >= KillThresholds[2] ? 3 : kills >= KillThresholds[1] ? 2 : kills >= KillThresholds[0] ? 1 : 0;

    /// <summary>tier -> bonus as a fraction of the wielder's natural stat (PA / MA).
    /// Deliberately CONSERVATIVE: an investment mechanic must start under-tuned, because nerfing
    /// earned (kill-grown) power is the most-hated kind of nerf. Easier to buff up than claw back.</summary>
    public static readonly double[] Factor = { 0.00, 0.10, 0.20, 0.30 };

    /// <summary>Speed grows gentler still -- it double-dips (damage AND turn frequency).</summary>
    public static readonly double[] SpeedFactor = { 0.00, 0.05, 0.10, 0.15 };

    /// <summary>tier -> the 2-char name suffix painted on the card ("  " renders as nothing).</summary>
    public static readonly string[] Suffix = { "  ", "+ ", "+2", "+3" };

    /// <summary>Caster gear grows Magick Attack instead of Physical (a mage kills with spells).</summary>
    public static bool IsCaster(string category) => category == "Rod" || category == "Staff";

    /// <summary>Missing-HP formulas ignore every stat -> no growth lever.</summary>
    public static bool SkipFormula(int formula) => formula == 67 || formula == 69;

    /// <summary>Speed-scaling weapons (Swiftfang / Swiftedge).</summary>
    public static bool IsSpeedFormula(int formula) => formula == 99;

    /// <summary>Magic-cast weapons (magic guns) scale off Magick Attack.</summary>
    public static bool IsMagicCastFormula(int formula) => formula == 4;
}
