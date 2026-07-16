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

    /// <summary>LW-95: hasten only a Doom RISING EDGE (doomed &amp;&amp; !wasDoomed) that is
    /// ATTRIBUTED to the wielder's own action (the caller's actingMainHand + actedByte==1 gate),
    /// and only ever write DOWN (countdown still above target). Fires once per Doom appearance,
    /// only when the acting main hand was the Eclipsebolt during its acted period; a pre-existing
    /// Doom (no edge) or a foe Doomed by any other source (another weapon's proc, an enemy cast)
    /// is left alone -- fail-closed toward the design: the bow's own May-inflict-Doom procs only.</summary>
    public static bool ShouldHasten(bool doomed, bool wasDoomed, int countdown, int target, bool attributed) =>
        doomed && !wasDoomed && attributed && countdown > target;
}
