namespace LivingWeapon;

/// <summary>Pure decisions for Staff of the Magi's "Sanctuary" signature. The per-tick crystal-counter pin and bearer-alive gate are in Sanctuary.cs.</summary>
internal sealed partial class Sanctuary
{
    /// <summary>True when the signature is configured (AntiCrystallize set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.AntiCrystallize;

    /// <summary>Only the main hand (RRHand) activates the gift, though kills count from any hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;
}
