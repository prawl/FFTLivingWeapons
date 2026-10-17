using System;

namespace LivingWeapon;

/// <summary>
/// Wraps a <see cref="GuardLandmark"/> and reports once when it reads Unreadable for a
/// long consecutive run. Without it, a mod that cannot read the game at all (unresolved
/// ASLR base, unmapped image) retried silently forever. A decorator keeps
/// <see cref="FingerprintGuard"/> portable and game-agnostic; the "how long is too long" policy
/// lives in the adapter (LaunchGuard.Stall.cs). Readings pass through unchanged.
/// </summary>
internal static class LandmarkStall
{
    /// <summary>Wraps <paramref name="inner"/> with the same Name and readings. Unreadable extends a
    /// streak, any other verdict resets it. At <paramref name="stallSteps"/>,
    /// <paramref name="onStalled"/> gets the landmark name and the tripping reading's Detail; it
    /// fires again only after the streak breaks and rebuilds. An Unreadable that
    /// <paramref name="counts"/> rejects is a deliberate wait, so it breaks the streak like any other verdict.</summary>
    public static GuardLandmark Watch(GuardLandmark inner, int stallSteps, Action<string, string?> onStalled,
        Func<LandmarkReading, bool>? counts = null)
    {
        if (stallSteps < 1) throw new ArgumentOutOfRangeException(nameof(stallSteps), "must be at least 1");
        int streak = 0;
        return new GuardLandmark(inner.Name, () =>
        {
            var reading = inner.Probe();
            if (reading.Verdict == LandmarkVerdict.Unreadable && (counts?.Invoke(reading) ?? true))
            {
                streak++;
                if (streak == stallSteps) onStalled(inner.Name, reading.Detail);
            }
            else streak = 0;
            return reading;
        });
    }
}
