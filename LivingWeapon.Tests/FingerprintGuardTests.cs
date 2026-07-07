using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The portable retry-until-decidable arming state machine (LW-50), tested with plain landmark
/// stubs (no game memory at all: FingerprintGuard.cs has zero project dependencies, so its own
/// tests stay just as portable). Covers: all-match arms once, any-mismatch stands down after a
/// consecutive-mismatch debounce, Unreadable retries forever, both terminal states stop probing
/// and never fire their callback twice, and the mismatch counter resets on any mismatch-free step.
/// </summary>
public class FingerprintGuardTests
{
    /// <summary>A controllable landmark stub: Verdict can be changed between Step() calls (for the
    /// counter-reset test) and CallCount proves Step() probes it exactly once per call while
    /// Verifying, never again once terminal.</summary>
    private sealed class FixedLandmark
    {
        public int CallCount { get; private set; }
        public LandmarkVerdict Verdict { get; set; }
        public GuardLandmark Landmark { get; }

        public FixedLandmark(string name, LandmarkVerdict verdict)
        {
            Verdict = verdict;
            Landmark = new GuardLandmark(name, () => { CallCount++; return Verdict; });
        }
    }

    [Fact]
    public void AllMatch_arms_and_fires_onArmed_once()
    {
        var a = new FixedLandmark("a", LandmarkVerdict.Match);
        var b = new FixedLandmark("b", LandmarkVerdict.Match);
        int armedCount = 0;
        var guard = new FingerprintGuard(new[] { a.Landmark, b.Landmark },
            onStandDown: _ => Assert.Fail("should not stand down"),
            onArmed: () => armedCount++,
            mismatchDebounce: 3);

        guard.Step();

        Assert.Equal(GuardState.Armed, guard.State);
        Assert.True(guard.Armed);
        Assert.Equal(1, armedCount);
    }

    [Fact]
    public void AnyMismatch_stands_down_after_debounce_and_fires_onStandDown_once()
    {
        var a = new FixedLandmark("a", LandmarkVerdict.Mismatch);
        int standDownCount = 0;
        string? diag = null;
        var guard = new FingerprintGuard(new[] { a.Landmark },
            onStandDown: d => { standDownCount++; diag = d; },
            mismatchDebounce: 3);

        guard.Step();
        guard.Step();
        Assert.Equal(GuardState.Verifying, guard.State);

        guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.Equal(1, standDownCount);
        Assert.Contains("a", diag);

        guard.Step();   // terminal: no second stand-down
        Assert.Equal(1, standDownCount);
    }

    [Fact]
    public void Unreadable_keeps_verifying_and_retries()
    {
        var a = new FixedLandmark("a", LandmarkVerdict.Unreadable);
        var guard = new FingerprintGuard(new[] { a.Landmark },
            onStandDown: _ => Assert.Fail("should not stand down"),
            mismatchDebounce: 3);

        guard.Step();
        guard.Step();
        guard.Step();

        Assert.Equal(GuardState.Verifying, guard.State);
        Assert.Equal(3, a.CallCount);
    }

    [Fact]
    public void MixedMatchUnreadable_stays_verifying()
    {
        var a = new FixedLandmark("a", LandmarkVerdict.Match);
        var b = new FixedLandmark("b", LandmarkVerdict.Unreadable);
        var guard = new FingerprintGuard(new[] { a.Landmark, b.Landmark },
            onStandDown: _ => Assert.Fail("should not stand down"),
            mismatchDebounce: 3);

        guard.Step();

        Assert.Equal(GuardState.Verifying, guard.State);
    }

    [Fact]
    public void Armed_is_sticky_and_stops_probing()
    {
        var a = new FixedLandmark("a", LandmarkVerdict.Match);
        var guard = new FingerprintGuard(new[] { a.Landmark },
            onStandDown: _ => Assert.Fail("should not stand down"),
            mismatchDebounce: 3);

        guard.Step();
        Assert.Equal(GuardState.Armed, guard.State);
        Assert.Equal(1, a.CallCount);

        guard.Step();
        guard.Step();

        Assert.Equal(1, a.CallCount);   // never probed again
        Assert.Equal(GuardState.Armed, guard.State);
    }

    [Fact]
    public void StoodDown_is_sticky()
    {
        var a = new FixedLandmark("a", LandmarkVerdict.Mismatch);
        int standDownCount = 0;
        var guard = new FingerprintGuard(new[] { a.Landmark },
            onStandDown: _ => standDownCount++,
            mismatchDebounce: 1);

        guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.Equal(1, a.CallCount);

        guard.Step();
        guard.Step();

        Assert.Equal(1, a.CallCount);   // never probed again
        Assert.Equal(1, standDownCount);
    }

    [Fact]
    public void MismatchCounter_resets_on_a_mismatch_free_step()
    {
        // N = 3. a toggles Mismatch/Match; b stays Unreadable so the "reset" step is a MIXED
        // Unreadable/Match step (an all-Match step would arm instead, per the review fix note).
        var a = new FixedLandmark("a", LandmarkVerdict.Mismatch);
        var b = new FixedLandmark("b", LandmarkVerdict.Unreadable);
        var guard = new FingerprintGuard(new[] { a.Landmark, b.Landmark },
            onStandDown: _ => { },
            mismatchDebounce: 3);

        // N-1 mismatch steps.
        guard.Step();
        guard.Step();
        Assert.Equal(GuardState.Verifying, guard.State);

        // One mismatch-free (mixed Unreadable/Match) step: must NOT arm, and must reset the counter.
        a.Verdict = LandmarkVerdict.Match;
        guard.Step();
        Assert.Equal(GuardState.Verifying, guard.State);

        // N-1 mismatch steps again: if the counter had NOT reset this would already stand down.
        a.Verdict = LandmarkVerdict.Mismatch;
        guard.Step();
        guard.Step();
        Assert.Equal(GuardState.Verifying, guard.State);

        // The Nth consecutive mismatch step: now it stands down.
        guard.Step();
        Assert.Equal(GuardState.StoodDown, guard.State);
    }

    [Fact]
    public void Diagnostic_names_the_failing_landmarks()
    {
        var a = new FixedLandmark("landmark-a", LandmarkVerdict.Mismatch);
        var b = new FixedLandmark("landmark-b", LandmarkVerdict.Mismatch);
        var c = new FixedLandmark("landmark-c", LandmarkVerdict.Match);
        string? diag = null;
        var guard = new FingerprintGuard(new[] { a.Landmark, b.Landmark, c.Landmark },
            onStandDown: d => diag = d,
            mismatchDebounce: 1);

        guard.Step();

        Assert.Equal(GuardState.StoodDown, guard.State);
        Assert.NotNull(diag);
        Assert.Contains("landmark-a", diag);
        Assert.Contains("landmark-b", diag);
        Assert.DoesNotContain("landmark-c", diag);
    }
}
