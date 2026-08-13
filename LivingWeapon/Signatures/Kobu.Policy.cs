using System;

namespace LivingWeapon;

/// <summary>
/// Pure decisions behind Kiyomori's "Kobu" signature -- no memory access.
/// The stateful wielder-locate and HP-diff enemy scan live in Kobu.cs.
/// </summary>
internal sealed partial class Kobu
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.BraveOneUp;

    /// <summary>One-shot raise: returns the value to write to the wielder's current-brave byte,
    /// or 0 for "no write". A failed/insane wielder read (below 1 or above 100) never triggers a
    /// write. A struck foe whose current brave does not exceed the wielder's LIVE current brave
    /// is a no-op. Otherwise the candidate is min(cap, struckLive) -- and if that clamp lands at
    /// or below wielderLive (e.g. struck 100, wielder 97, cap 97), it is STILL a no-op: this is a
    /// one-shot raise, never a lateral or lowering write.</summary>
    public static int OneShotRaise(int wielderLive, int struckLive, int cap)
    {
        if (wielderLive < 1 || wielderLive > 100) return 0;
        if (struckLive <= wielderLive) return 0;
        int clamped = Math.Min(cap, struckLive);
        return clamped > wielderLive ? clamped : 0;
    }
}
