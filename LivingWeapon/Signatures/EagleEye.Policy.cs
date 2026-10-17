namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Eagle Eye; the stateful scan and write live in EagleEye.cs.
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

    /// <summary>hasten only a Doom rising edge (doomed &amp;&amp; !wasDoomed) attributed to the wielder's own
    /// action (actingMainHand and actedByte==1), and only write down. Pre-existing Doom or Doom from other sources is left alone.</summary>
    public static bool ShouldHasten(bool doomed, bool wasDoomed, int countdown, int target, bool attributed) =>
        doomed && !wasDoomed && attributed && countdown > target;
}
