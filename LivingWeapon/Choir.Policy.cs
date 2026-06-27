namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Warlock's Staff's "Choir" signature -- no memory access.
/// The stateful per-tick bearer writer lives in Choir.cs.
/// </summary>
internal sealed partial class Choir
{
    /// <summary>True when the signature is configured (InstantCastRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.InstantCastRadius > 0;

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;
}
