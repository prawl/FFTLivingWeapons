using System;

namespace LivingWeapon;

/// <summary>
/// Pure decisions behind Kiyomori's "Kobu" signature -- no memory access.
/// The stateful wielder-locate, HP-diff enemy scan, and per-tick brave hold live in Kobu.cs.
/// </summary>
internal sealed partial class Kobu
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.BraveOneUp;

    /// <summary>Climb-only, capped ceiling: the new max is the higher of curMax and struckBrave,
    /// but never above cap. A foe with lower brave never drags the ceiling down.</summary>
    public static int NextMax(int curMax, int struckBrave, int cap)
        => Math.Min(cap, Math.Max(curMax, struckBrave));

    /// <summary>True when the wielder's live current brave is below the accumulated target and
    /// should be raised. Never writes a value below the live brave (climb-only guarantee).</summary>
    public static bool ShouldRaise(int liveBrave, int target) => liveBrave < target;
}
