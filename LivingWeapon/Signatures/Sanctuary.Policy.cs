namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Staff of the Magi's "Sanctuary" signature -- no memory access.
/// The stateful per-tick crystal-counter pin and the bearer-alive gate live in Sanctuary.cs.
/// </summary>
internal sealed partial class Sanctuary
{
    /// <summary>True when the signature is configured (AntiCrystallize set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.AntiCrystallize;

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;
}
