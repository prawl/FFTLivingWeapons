namespace LivingWeapon;

/// <summary>
/// Pure decisions for Warlock's Staff's "Choir" signature, no memory access.
/// The per-tick bearer writer is in Choir.cs.
/// </summary>
internal sealed partial class Choir
{
    /// <summary>True when the signature is configured (InstantCastRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.InstantCastRadius > 0;

    /// <summary>Main-hand only. The weapon must be in RRHand. Kills count from any hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;
}
