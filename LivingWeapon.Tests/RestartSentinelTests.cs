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

    // LW-233 live-drill residual (2026-08-17): PresentRevive's identity-tuple params. SameId/SameId
    // models "the revived unit IS the one this slot was credited to" (the ordinary case for every
    // test below that isn't specifically about the exemption); OtherId as the credited half models
    // "no genuine prior credit matches this presentation" (a fresh battle, or a different
    // encounter's unit after LW-108's starved-bracket hole) -- used only where a test's whole point
    // is that grace alone must still hold.
    //
    // FINDING 0 (2026-08-17 verifier correction): PresentRevive also takes a nameId pair now --
    // SameNameId/SameNameId pairs with SameId/SameId everywhere below (a genuine match needs BOTH
    // the tuple and the nameId to agree); OtherNameId pairs with OtherId the same way. Neither
    // constant is ever 0 -- 0 is the fail-closed "unavailable" sentinel, covered by its own
    // dedicated tests below (A_zero_nameId_never_opens_even_with_a_matching_tuple's KillTracker-
    // level twin lives in KillTrackerRestartTests.cs; the pure PresentRevive-level pin is
    // A_zero_nameId_never_substitutes_for_the_tuple_match below).
    private static readonly (byte lvl, byte br, byte fa, ushort mhp) SameId = (10, 50, 50, 400);
    private static readonly (byte lvl, byte br, byte fa, ushort mhp) OtherId = (99, 89, 76, 352);
    private const ushort SameNameId = 555;
    private const ushort OtherNameId = 777;

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

        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
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

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void A_null_that_arrives_after_the_revive_never_retroactively_opens()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);

        // Revive first, while the sentinel has never seen a null.
        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));

        // The qualifying null arrives only now -- too late for the revive already presented (and
        // there was no in-progress streak at presentation time to stash against either).
        Advance(s, Persist, rawNull: true);
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void Grace_holds_even_with_a_qualified_null_and_a_joining_revive_of_a_different_identity()
    {
        // LW-233 live-drill residual (2026-08-17): grace is now exempted by a MATCHING identity
        // (see Opens_before_grace_when_the_revived_identity_matches_the_credited_one below), so
        // this test now specifically pins the mismatched-identity half -- grace alone still guards
        // a genuinely different unit (a fresh battle, or LW-108's starved-bracket hole), which is
        // the whole reason the exemption is identity-GATED rather than unconditional.
        var s = new RestartSentinel();
        // Land battleAgeTicks EXACTLY at Grace (not past it: > Grace is required to open) once the
        // null has also qualified.
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, OtherId, SameId, OtherNameId, SameNameId));
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void Opens_before_grace_when_the_revived_identity_matches_the_credited_one()
    {
        // Same shape as the mismatched-identity test above (battleAgeTicks lands EXACTLY at Grace,
        // not past it), differing only in identity -- proves the exemption is real at the
        // RestartSentinel level, not just in the pure Policy table.
        var s = new RestartSentinel();
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);

        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.True(s.LatchOpen);
    }

    // ---- FINDING 0 (2026-08-17 verifier correction): the nameId half is REQUIRED, not optional ----

    [Fact]
    public void A_matching_tuple_with_a_mismatched_nameId_never_opens_the_twin_collision_case()
    {
        // The exact case FINDING 0 exists for: docs/LIVE_LEDGER.md row [party-nameid-unique-key]
        // proves two real, distinct units can share the SAME (lvl,br,fa,maxHp) tuple. Same shape as
        // Opens_before_grace_when_the_revived_identity_matches_the_credited_one above (battle age
        // AT grace, not past it) but the nameId half disagrees -- must refuse even though the tuple
        // half alone would have matched.
        var s = new RestartSentinel();
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, OtherNameId));
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void A_zero_nameId_never_substitutes_for_the_tuple_match()
    {
        // Fail-closed: a matching tuple with an UNAVAILABLE (zero) nameId on either side must never
        // open, even though the tuple half alone would have matched. 0 is this codebase's existing
        // "no resolved identity" sentinel (both "genuinely unreadable" and "genuinely zero" collapse
        // to it), so treating it as a wildcard match would reopen exactly the false-positive risk
        // requiring nameId was meant to close.
        var s = new RestartSentinel();
        Advance(s, Grace - Persist);
        Advance(s, Persist, rawNull: true);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, 0, SameNameId));
        Assert.False(s.LatchOpen);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, 0));
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void A_sustained_out_of_live_stretch_closes_the_latch_and_re_arms_the_grace()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.True(s.LatchOpen);

        Advance(s, RearmTicks, inLiveish: false);   // sustained off-field/out-of-live stretch
        Assert.False(s.LatchOpen);

        // Grace re-armed: an otherwise-qualifying join right after the stretch still refuses,
        // because the null history was cleared too (haveQualifiedNull is now false) AND the
        // battle age reset below the grace floor. Mismatched identity (OtherId) so the LW-233
        // exemption cannot paper over the grace check this specific assertion is pinning.
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, OtherId, SameId, OtherNameId, SameNameId));

        // But grace can be re-earned exactly like a fresh battle: past grace again, a fresh
        // qualified null opens normally.
        Advance(s, Grace + 1);
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
    }

    [Fact]
    public void The_latch_closes_after_its_duration_and_can_re_open_on_a_fresh_join()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.True(s.LatchOpen);

        Advance(s, LatchDuration);   // let the full duration elapse with no re-arming event
        Assert.False(s.LatchOpen);

        // A fresh qualifying join re-opens it.
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_second_qualifying_revive_while_already_open_re_arms_the_full_duration()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));

        Advance(s, LatchDuration - 1);   // one tick from expiry
        Assert.True(s.LatchOpen);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));   // re-arms

        Advance(s, LatchDuration - 1);   // would have expired under the OLD countdown
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_status_death_revive_never_opens_even_while_the_latch_is_already_open()
    {
        // Named residual: healedFromZero==false must refuse regardless of latch state.
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.True(s.LatchOpen);

        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: false, SameId, SameId, SameNameId, SameNameId));
    }

    [Fact]
    public void ResetBattle_clears_every_field_including_an_open_latch()
    {
        var s = new RestartSentinel();
        PastGraceWithQualifiedNull(s);
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.True(s.LatchOpen);

        s.ResetBattle();
        Assert.False(s.LatchOpen);

        // And grace is back in effect -- a null right after reset does not open the latch until
        // grace elapses again. Mismatched identity: ResetBattle is what clears KillTracker's own
        // _creditedIdentity in production, so a genuinely fresh battle never has a matching
        // credited identity to exempt against.
        Advance(s, Persist, rawNull: true);
        Assert.Equal(RevivePresentResult.Refuse, s.PresentRevive(0, W(1), false, healedFromZero: true, OtherId, SameId, OtherNameId, SameNameId));
    }

    [Fact]
    public void Injected_recorder_receives_one_restart_record_on_the_open_edge_only()
    {
        var recorded = new List<(string type, string payload)>();
        var s = new RestartSentinel((type, payload) => recorded.Add((type, payload)));
        PastGraceWithQualifiedNull(s);

        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.Equal(RevivePresentResult.UncreditNow, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));   // a second joining revive, same open latch

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

        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Stashed, verdict);
        Assert.False(s.LatchOpen);   // not decided yet -- neither open nor refused
    }

    [Fact]
    public void A_stashed_revive_drains_and_opens_the_latch_when_the_null_qualifies_next_tick()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1

        var verdict = s.PresentRevive(5, W(19), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);
        Assert.Equal(RevivePresentResult.Stashed, verdict);

        var drained = s.Tick(rawActorNull: true, inLiveish: true);   // streak -> 2, QUALIFIES

        Assert.Single(drained);
        Assert.Equal(5, drained[0].Slot);
        Assert.Equal(19, drained[0].Weapons[0]);
        Assert.True(s.LatchOpen);
    }

    // ---- Identity-gated stash drain (LW-233 live-drill residual, 2026-08-17) ----

    [Fact]
    public void A_stashed_revive_before_grace_drains_via_a_matching_identity_even_though_grace_never_clears()
    {
        // Everything below GraceTicks the whole way through -- no PastGrace helper at all -- and
        // the stash still both ACCEPTS (ShouldStash) and DRAINS (ProcessStash's per-entry canOpen)
        // purely on the matching identity. This is the exact live-tape shape: the out-of-live
        // re-arm never lets battle age clear grace, but the revived identity matches.
        var s = new RestartSentinel();
        Advance(s, 5);                   // nowhere near Grace (150)
        Advance(s, 1, rawNull: true);    // streak == 1, not yet qualified

        var verdict = s.PresentRevive(7, W(42), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);
        Assert.Equal(RevivePresentResult.Stashed, verdict);
        Assert.False(s.LatchOpen);

        var drained = s.Tick(rawActorNull: true, inLiveish: true);   // streak -> 2, QUALIFIES

        Assert.Single(drained);
        Assert.Equal(7, drained[0].Slot);
        Assert.Equal(42, drained[0].Weapons[0]);
        Assert.True(s.LatchOpen);
    }

    [Fact]
    public void A_would_be_stash_before_grace_with_a_mismatched_identity_refuses_outright()
    {
        // The mirror negative: same razor-thin timing, same everything, except the identity does
        // not match -- ShouldStash's own grace-or-identity gate (mirrors ShouldOpenLatch's) is
        // false on both counts here, so PresentRevive refuses immediately instead of stashing
        // something that could never legitimately drain.
        var s = new RestartSentinel();
        Advance(s, 5);
        Advance(s, 1, rawNull: true);

        var verdict = s.PresentRevive(7, W(42), false, healedFromZero: true, OtherId, SameId, OtherNameId, SameNameId);
        Assert.Equal(RevivePresentResult.Refuse, verdict);
        Assert.False(s.LatchOpen);
    }

    [Fact]
    public void A_stashed_revive_whose_null_breaks_before_qualifying_drops_silently()
    {
        var s = new RestartSentinel();
        Advance(s, Grace + 1);
        Advance(s, 1, rawNull: true);   // streak == 1
        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);
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

        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(1, W(10), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));
        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(2, W(11), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));

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
        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId);   // streak == 0 -- not even in-progress
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
        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));

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
        Assert.Equal(RevivePresentResult.Stashed, s.PresentRevive(0, W(1), false, healedFromZero: true, SameId, SameId, SameNameId, SameNameId));

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

        var verdict = s.PresentRevive(0, W(1), false, healedFromZero: false, SameId, SameId, SameNameId, SameNameId);

        Assert.Equal(RevivePresentResult.Refuse, verdict);
        var drained = s.Tick(rawActorNull: true, inLiveish: true);   // qualifies -- nothing was ever stashed
        Assert.Empty(drained);
    }
}
