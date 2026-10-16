using System;

namespace LivingWeapon;

/// <summary>
/// LW-392: a decorator over a <see cref="GuardLandmark"/> that notices when the landmark has
/// read Unreadable for a long CONSECUTIVE run and tells the adapter once. Before this, a mod
/// that could not read the game at all (the LW-387 symptom: an unresolved ASLR base, or any
/// unmapped image) retried silently forever and the player saw nothing.
///
/// WHY a decorator and not a change inside <see cref="FingerprintGuard"/>: the core is a
/// copy-the-file portable unit (see its class doc) and stays game-agnostic and unchanged; the
/// wall-clock-ish "how long is too long" policy belongs to the adapter (LaunchGuard.Stall.cs).
/// The readings pass through untouched, so the core's arm / debounce / stand-down behavior is
/// exactly what it was.
/// </summary>
internal static class LandmarkStall
{
    /// <summary>Wraps <paramref name="inner"/>: same Name, same readings. Each Unreadable
    /// increments a streak counter, any other verdict resets it to 0. When the streak reaches
    /// exactly <paramref name="stallSteps"/>, <paramref name="onStalled"/> is invoked once with
    /// the landmark name and the Detail of the reading that tripped it (an adapter may use it to
    /// say WHY the read was unreadable); it fires again only if the streak breaks and climbs
    /// back.</summary>
    public static GuardLandmark Watch(GuardLandmark inner, int stallSteps, Action<string, string?> onStalled)
    {
        if (stallSteps < 1) throw new ArgumentOutOfRangeException(nameof(stallSteps), "must be at least 1");
        int streak = 0;
        return new GuardLandmark(inner.Name, () =>
        {
            var reading = inner.Probe();
            if (reading.Verdict == LandmarkVerdict.Unreadable)
            {
                streak++;
                if (streak == stallSteps) onStalled(inner.Name, reading.Detail);
            }
            else streak = 0;
            return reading;
        });
    }
}
