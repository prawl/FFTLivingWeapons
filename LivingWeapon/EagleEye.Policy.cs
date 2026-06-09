namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Eagle Eye -- no memory access, so they're unit-tested directly.
/// The stateful band scan + countdown write lives in EagleEye.cs.
/// </summary>
internal sealed partial class EagleEye
{
    /// <summary>The countdown value to force a Doom down to while this signature is active, or 0
    /// (inactive). Active = a doom-hasten signature (DoomCountdownTo &gt; 0) whose AtTier is earned.</summary>
    public static int AuraTarget(WeaponSignature? sig, int tier)
    {
        if (sig is null || sig.DoomCountdownTo <= 0) return 0;
        if (tier < sig.AtTier) return 0;
        return sig.DoomCountdownTo;
    }

    /// <summary>Idempotent hasten rule: write only when the enemy is Doomed AND its countdown is
    /// STILL above the target. Once at/below target we never touch it -- the engine ticks it down
    /// to 0 (death) untouched, and an expired or undead-immune Doom (bit clears, no kill) is left be.</summary>
    public static bool ShouldHasten(bool doomed, int countdown, int target) =>
        doomed && countdown > target;
}
