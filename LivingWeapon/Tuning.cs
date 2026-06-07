namespace LivingWeapon;

/// <summary>
/// Living-Weapon growth tuning. Kept in one place so detection, growth, and
/// display agree. Thresholds are deliberately LOW right now (1/2/3 kills) for
/// fast in-game verification -- raise them once the loop is proven.
/// </summary>
internal static class Tuning
{
    /// <summary>kills -> tier (0..3), checked high to low.</summary>
    public static int TierFor(int kills) => kills >= 3 ? 3 : kills >= 2 ? 2 : kills >= 1 ? 1 : 0;

    /// <summary>tier -> bonus as a fraction of the wielder's natural stat (PA / MA).</summary>
    public static readonly double[] Factor = { 0.00, 0.25, 0.50, 0.75 };

    /// <summary>Speed grows gentler -- it double-dips (damage AND turn frequency).</summary>
    public static readonly double[] SpeedFactor = { 0.00, 0.10, 0.20, 0.30 };

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
