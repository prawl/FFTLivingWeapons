namespace LivingWeapon;

/// <summary>
/// LW-149 stage C: the shared activation-edge detector, extracted from the 14+2-site idiom
/// <c>if (active != _wasActive) { _wasActive = active; &lt;module's own log call&gt;; }</c> that every
/// signature module hand-rolled around its own <c>_wasActive</c> field. This class owns ONLY edge
/// detection -- it never logs and never knows what a module's log line says. Each caller keeps its
/// own log call, guarded by this method's return value, so the extraction is byte-identical by
/// construction: no log string, tier, or cadence moved.
/// </summary>
internal static class ActivationEdge
{
    /// <summary>True exactly on a transition (<paramref name="active"/> differs from
    /// <paramref name="wasActive"/>), and -- matching the old inline <c>_wasActive = active;</c> --
    /// writes the new value back through <paramref name="wasActive"/> as a side effect. False on
    /// steady state, leaving <paramref name="wasActive"/> untouched.</summary>
    public static bool Step(ref bool wasActive, bool active)
    {
        if (active == wasActive) return false;
        wasActive = active;
        return true;
    }
}
