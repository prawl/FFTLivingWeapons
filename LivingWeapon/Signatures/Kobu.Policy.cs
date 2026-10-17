using System;

namespace LivingWeapon;

/// <summary>
/// Pure decisions behind Kiyomori's "Kobu" signature. The stateful scan lives in Kobu.cs.
/// </summary>
internal sealed partial class Kobu
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.BraveOneUp;

    /// <summary>One-shot raise: returns the value to write to the wielder's current-brave byte,
    /// or 0 for no write. Never lowers or writes sideways: min(cap, struckLive) must exceed wielderLive,
    /// and a wielder read outside 1..100 is ignored.</summary>
    public static int OneShotRaise(int wielderLive, int struckLive, int cap)
    {
        if (wielderLive < 1 || wielderLive > 100) return 0;
        if (struckLive <= wielderLive) return 0;
        int clamped = Math.Min(cap, struckLive);
        return clamped > wielderLive ? clamped : 0;
    }
}
