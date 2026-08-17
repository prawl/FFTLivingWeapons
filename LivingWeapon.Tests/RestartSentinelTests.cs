using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-233: the stateful half of RestartSentinel -- Tick()'s null-streak/battle-age/out-of-live
/// bookkeeping, PresentRevive's latch open/re-open/close machinery, AND (the LW-233 verifier-
/// caught fix) the DEFERRED-VERDICT stash: a credited heal-from-zero revive presented while a
/// null is mid-flight but not yet qualified (the 0ms-join shape, retry A's real tape) is held,
/// not dropped, until the streak either qualifies (drains through the Tick() return value) or
/// breaks (forgotten silently). No memory involved (the sentinel takes plain values);
/// RestartSentinelPolicyTests.cs covers the pure ShouldOpenLatch/ShouldStash tables this drives.
/// KillTracker.Corpses integration (the tape-shape tests) lives in KillTrackerRestartTests.cs.
/// </summary>
public class RestartSentinelTests
{
    private const int Grace = RestartSentinelPolicy.GraceTicks;
    private const int Persist = RestartSentinelPolicy.NullPersistTicks;
    private const int Window = RestartSentinelPolicy.JoinWindowTicks;
    private const int LatchDuration = RestartSentinelPolicy.LatchTicks;
    private const int RearmTicks = RestartSentinelPolicy.OutOfLiveRearmTicks;

    /// <summary>Advance n ticks with the given raw-null/inLiveish inputs held constant. Returns
    /// the LAST tick's drained-stash list (only the final call's drain is usually interesting).</summary>
    private static IReadOnlyList<(int Slot, List<int> Weapons, bool ViaFallback)> Advance(
        RestartSentinel s, int n, bool rawNull = false, bool inLiveish = true)
    {
        IReadOnlyList<(int, List<int>, bool)> last = System.Array.Empty<(int, List<int>, bool)>();
        for (int i = 0; i < n; i++) last = s.Tick(rawNull, inLiveish);
        return last;
    }

    /// <summary>Past grace, with a freshly-qualified null (persisted exactly NullPersistTicks).</summary>
    private static void PastGraceWithQualifiedNull(RestartSentinel s)
    {
        Advance(s, Grace + 1);
        Advance(s, Persist, rawNull: true);
    }

    private static List<int> W(int id) => new() { id };

    [Fact]
    public void A_qualified_null_and_a_joining_credited_revive_opens_the_latch()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);

        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_null_alone_with_no_revive_never_opens()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Advance(s, 5);   // time passes, no revive ever presented

        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void Revives_alone_with_no_null_never_open()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void A_null_that_arrives_after_the_revive_never_retroactively_opens()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);

        // Revive first, while the sentinel has never seen a null.
        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true));

        // The qualifying null arrives only now -- too late for the revive already presented (and
        // there was no in-progress streak at presentation time to stash against either).
        Advance(s, Persist, rawNull: true);
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void Grace_holds_even_with_a_qualified_null_and_a_joining_revive()
    {
        var s = new RestartSentinel();
        // Land battleAgeTicks EXACTLY at Grace (not past it: > Grace is required to open) once the
        // null has also qualified.
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void A_sustained_out_of_live_stretch_closes_the_latch_and_re_arms_the_grace()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.True(s.LatchOpen);

        Advance(s, RearmTicks, inLiveish: false);   // sustained off-field/out-of-live stretch
        Assert.False(s.LatchOpen);

        // Grace re-armed: an otherwise-qualifying join right after the stretch still refuses,
        // because the null history was cleared too (haveQualifiedNull is now false) AND the
        // battle age reset below the grace floor.
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true));

        // But grace can be re-earned exactly like a fresh battle: past grace again, a fresh
        // qualified null opens normally.
        Advance(s, Grace + 1);
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
    }

    [Fact]
    public void The_latch_closes_after_its_duration_and_can_re_open_on_a_fresh_join()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.True(s.LatchOpen);

        Advance(s, LatchDuration);   // let the full duration elapse with no re-arming event
        Assert.False(s.LatchOpen);

        // A fresh qualifying join re-opens it.
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_second_qualifying_revive_while_already_open_re_arms_the_full_duration()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));

        Advance(s, LatchDuration - 1);   // one tick from expiry
        Assert.True(s.LatchOpen);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));   // re-arms

        Advance(s, LatchDuration - 1);   // would have expired under the OLD countdown
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_status_death_revive_never_opens_even_while_the_latch_is_already_open()
    {
        // Named residual: healedFromZero==false must refuse regardless of latch state.
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.True(s.LatchOpen);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: false));
    }

    [Fact]
    public void ResetBattle_clears_every_field_including_an_open_latch()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.True(s.LatchOpen);

        s.ResetBattle();
        Assert.False(s.LatchOpen);

        // And grace is back in effect -- a null right after reset does not open the latch until
        // grace elapses again.
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true));
    }

    [Fact]
    public void Injected_recorder_receives_one_restart_record_on_the_open_edge_only()
    {
        var recorded = new List<(string type, string payload)>();
        var s = new RestartSentinel((type, payload) => recorded.Add((type, payload)));
        PastGraceWithQualifiedNull(s);

        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true));   // a second joining revive, same open latch

        var restartRecords = recorded.FindAll(r => r.type == "restart");
        Assert.Single(restartRecords);
        Assert.Contains("latch open", restartRecords[0].payload);
    }

    // ---- DEFERRED VERDICT (LW-233 fix: the 0ms-join shape) ----

    [Fact]
    public void A_revive_presented_while_the_null_is_one_tick_in_is_stashed_not_refused()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // the null just started -- streak == 1, not yet qualified

        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: true);

        Assert.Equal(RevivePresentResult.Stashed, verdict);
        Assert.False(s.LatchOpen);   // not decided yet -- neither open nor refused
    }

    [Fact]
    public void A_stashed_revive_drains_and_opens_the_latch_when_the_null_qualifies_next_tick()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1

        var verdict = s.PresentRevive(5, W(19), false, healedFromZero: true);
        Assert.Equal(RevivePresentResult.Stashed, verdict);

        var drained = s.Tick(rawActorNull: true, inLiveish: true);   // streak -> 2, QUALIFIES

        Assert.Single(drained);
        Assert.Equal(5, drained[0].Slot);
        Assert.Equal(19, drained[0].Weapons[0]);
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_stashed_revive_whose_null_breaks_before_qualifying_drops_silently()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1
        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: true);
        Assert.Equal(RevivePresentResult.Stashed, verdict);

        var drained = s.Tick(rawActorNull: false, inLiveish: true);   // the flicker breaks -- streak resets to 0

        Assert.Empty(drained);
        Assert.False(s.LatchOpen);

        // Confirm it stays gone -- even if a LATER, unrelated null eventually qualifies, the
        // original stash never resurfaces.
        drained = Advance(s, Persist, rawNull: true);
        Assert.Empty(drained);
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void Multiple_revives_stashed_together_all_drain_on_the_same_qualifying_tick()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1

        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(1, W(10), false, healedFromZero: true));
        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(2, W(11), false, healedFromZero: true));

        var drained = s.Tick(rawActorNull: true, inLiveish: true);   // qualifies

        Assert.Equal(2, drained.Count);
        Assert.Contains(drained, d => d.Slot == 1 && d.Weapons[0] == 10);
        Assert.Contains(drained, d => d.Slot == 2 && d.Weapons[0] == 11);
    }

    [Fact]
    public void A_revive_presented_with_no_null_streak_in_progress_refuses()
    {
        // NOT a grace-gate pin (that lives directly in RestartSentinelPolicyTests'
        // Never_stashes_before_the_battle_age_grace_has_elapsed): here nullStreakTicks is 0 (no
        // null was ever observed), so ShouldStash's own in-progress check short-circuits before
        // battleAgeTicks vs Grace is even evaluated. Landing before grace too is incidental, not
        // what this test isolates.
        var s = new RestartSentinel();
        Advance(s, Grace - 1);
        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: true);   // streak == 0 -- not even in-progress
        Assert.Equal(RevivePresentResult.Refuse, verdict);
    }

    [Fact]
    public void A_sustained_out_of_live_stretch_leaves_no_stash_to_drain()
    {
        // Belt-and-braces, not a unique pin on the out-of-live branch's explicit _stash.Clear():
        // the stashed entry's own TTL (NullPersistTicks == Persist) would already expire it within
        // 2 ticks of the null streak breaking, well before RearmTicks (60) elapses, so this test
        // cannot isolate WHICH mechanism emptied the stash. It confirms the observable outcome
        // holds regardless: nothing drains, and the re-armed grace refuses even a fresh qualifying
        // null afterward.
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1
        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(0, W(1), false, healedFromZero: true));

        Advance(s, RearmTicks, inLiveish: false);   // sustained out-of-live stretch

        var drained = Advance(s, Persist, rawNull: true);
        Assert.Empty(drained);
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void ResetBattle_clears_a_pending_stash()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);
        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(0, W(1), false, healedFromZero: true));

        s.ResetBattle();

        var drained = Advance(s, Grace + 1 + Persist, rawNull: true);   // fresh grace, fresh qualifying null
        Assert.Empty(drained);   // the old stash is gone -- nothing to drain
    }

    [Fact]
    public void A_status_death_revive_is_never_stashed()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1, in-progress

        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: false);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        var drained = s.Tick(rawActorNull: true, inLiveish: true);   // qualifies -- nothing was ever stashed
        Assert.Empty(drained);
    }
}
