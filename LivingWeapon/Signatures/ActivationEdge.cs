namespace LivingWeapon;

/// <summary>
/// LW-149 stage C: shared activation-edge detector replacing the per-module
/// <c>if (active != _wasActive)</c> blocks. It only detects edges; callers keep their own logging.
/// </summary>
internal static class ActivationEdge
{
    /// <summary>True on a transition, writing the new value back through <paramref name="wasActive"/>; false otherwise.</summary>
    public static bool Step(ref bool wasActive, bool active)
    {
        if (active == wasActive) return false;
        wasActive = active;
        return true;
    }
}
