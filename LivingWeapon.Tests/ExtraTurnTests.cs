using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Zwill Straightblade's +3 "extra turn on kill", v7: the grant is driven entirely off the killer's
/// own scheduler CT (the condensed active-unit struct follows cursor HOVER, so any "is the killer
/// acting" test is poisoned -- see FFTHandsFree BATTLE_COORDINATES.md). The engine pulls CT below
/// the consume floor in exactly one case: a turn of that unit ENDED. We read before every re-slam,
/// so pull-downs are observable events to COUNT: arm during the kill-turn (CT high) -> expect two
/// (kill-turn end, bonus end); arm after it (CT low) -> expect one (bonus end). Release on the last.
/// The memory orchestration (locate by wielder, slam, windows) is integration; this nails the
/// pure policy: classification, the refractory pull-down detector, and the counting machine.
/// </summary>
public class ExtraTurnTests
{
    // --- Classify: two consecutive agreeing reads decide how many pull-downs are owed ---

    [Fact]
    public void Classify_high_pair_means_kill_turn_in_progress()        // engine holds 100 during a turn
        => Assert.Equal(GrantState.Owed, ExtraTurn.Classify(100, 100));

    [Fact]
    public void Classify_tolerates_a_clamped_or_over_slammed_high_pair()
        => Assert.Equal(GrantState.Owed, ExtraTurn.Classify(105, 100));

    [Fact]
    public void Classify_low_pair_means_the_kill_turn_already_ended()   // late credit (ct=5 case, live)
        => Assert.Equal(GrantState.Pinning, ExtraTurn.Classify(5, 5));

    [Fact]
    public void Classify_low_pair_agrees_on_band_not_value()
        => Assert.Equal(GrantState.Pinning, ExtraTurn.Classify(0, 69));

    [Fact]
    public void Classify_disagreeing_reads_keep_sampling()              // torn struct / racing turn end
        => Assert.Null(ExtraTurn.Classify(100, 5));

    [Fact]
    public void Classify_unlocated_reads_keep_sampling()
    {
        Assert.Null(ExtraTurn.Classify(-1, 100));
        Assert.Null(ExtraTurn.Classify(100, -1));
        Assert.Null(ExtraTurn.Classify(-1, -1));
    }

    // --- Observe: the refractory pull-down detector (took needs a streak; a pull-down resets it) ---

    [Fact]
    public void Observe_latches_took_after_a_full_streak_of_high_reads()
    {
        var s = ExtraTurn.Observe(0, false, 100);
        Assert.Equal((1, false, false), s);
        s = ExtraTurn.Observe(s.streak, s.took, 105);
        Assert.Equal((2, false, false), s);
        s = ExtraTurn.Observe(s.streak, s.took, 100);
        Assert.True(s.took);
        Assert.False(s.pullDown);
    }

    [Fact]
    public void Observe_counts_a_pull_down_only_after_took()            // a low read pre-slam isn't a move
        => Assert.Equal((0, false, false), ExtraTurn.Observe(0, false, 5));

    [Fact]
    public void Observe_counts_the_engine_pulling_a_landed_slam()
        => Assert.Equal((0, false, true), ExtraTurn.Observe(3, true, 5));

    [Fact]
    public void Observe_ignores_the_wobble_band_but_keeps_took()        // 70..99 = wobble, not a move
        => Assert.Equal((0, true, false), ExtraTurn.Observe(3, true, 85));

    [Fact]
    public void Observe_at_the_consume_floor_is_wobble_not_a_move()
        => Assert.Equal((0, true, false), ExtraTurn.Observe(3, true, 70));

    [Fact]
    public void Observe_keeps_took_through_a_locate_gap()               // ct=-1 must not count or clear
        => Assert.Equal((0, true, false), ExtraTurn.Observe(3, true, -1));

    [Fact]
    public void Observe_refractory_blocks_double_counting_one_turn_end()
    {
        // Turn-end oscillation: engine reset interleaves with our slam: 0 (counted) -> 105 -> 0.
        var s = ExtraTurn.Observe(3, true, 0);                  // the real pull-down
        Assert.True(s.pullDown);
        s = ExtraTurn.Observe(s.streak, s.took, 105);           // our own write read back (streak 1)
        Assert.False(s.took);
        s = ExtraTurn.Observe(s.streak, s.took, 0);             // engine reset again -- NOT a second event
        Assert.False(s.pullDown);
    }

    // --- Step: count pull-downs; the last one is the consumed bonus ---

    [Fact]
    public void Owed_advances_to_pinning_on_the_kill_turns_end()
        => Assert.Equal((GrantState.Pinning, false), ExtraTurn.Step(GrantState.Owed, pullDown: true));

    [Fact]
    public void Owed_holds_until_the_kill_turn_ends()
        => Assert.Equal((GrantState.Owed, false), ExtraTurn.Step(GrantState.Owed, pullDown: false));

    [Fact]
    public void Pinning_releases_consumed_on_the_bonus_turns_end()
        => Assert.Equal((GrantState.Idle, true), ExtraTurn.Step(GrantState.Pinning, pullDown: true));

    [Fact]
    public void Pinning_holds_until_the_bonus_is_consumed()
        => Assert.Equal((GrantState.Pinning, false), ExtraTurn.Step(GrantState.Pinning, pullDown: false));

    [Fact]
    public void Idle_and_arming_do_not_step()
    {
        Assert.Equal((GrantState.Idle, false), ExtraTurn.Step(GrantState.Idle, pullDown: true));
        Assert.Equal((GrantState.Arming, false), ExtraTurn.Step(GrantState.Arming, pullDown: true));
    }

    // --- Slams / Healthy / RestoreCt: the orchestrator's per-tick decisions ---

    [Fact]
    public void Slams_only_while_a_pull_down_is_owed()                  // Arming reads must stay unpolluted
    {
        Assert.True(ExtraTurn.Slams(GrantState.Owed));
        Assert.True(ExtraTurn.Slams(GrantState.Pinning));
        Assert.False(ExtraTurn.Slams(GrantState.Arming));
        Assert.False(ExtraTurn.Slams(GrantState.Idle));
    }

    [Fact]
    public void Healthy_holds_refresh_the_no_signal_deadline()          // window = no-signal timeout,
    {                                                                   // NOT a phase budget
        Assert.True(ExtraTurn.Healthy(100));
        Assert.True(ExtraTurn.Healthy(105));
        Assert.False(ExtraTurn.Healthy(99));
        Assert.False(ExtraTurn.Healthy(-1));
    }

    [Fact]
    public void Every_release_except_consumed_restores_the_parked_ct()  // a parked 105 ghost-grants later
    {
        Assert.False(ExtraTurn.RestoreCt(ReleaseReason.Consumed));
        Assert.True(ExtraTurn.RestoreCt(ReleaseReason.NoSignal));
        Assert.True(ExtraTurn.RestoreCt(ReleaseReason.AbsoluteCap));
        Assert.True(ExtraTurn.RestoreCt(ReleaseReason.KillerDead));
        Assert.True(ExtraTurn.RestoreCt(ReleaseReason.BattleReset));
        Assert.True(ExtraTurn.RestoreCt(ReleaseReason.GateLost));
    }

    // --- End-to-end traces through the pure policy (the two live credit timings) ---

    [Fact]
    public void Mid_turn_credit_takes_two_pull_downs_to_consume()
    {
        Assert.Equal(GrantState.Owed, ExtraTurn.Classify(100, 100));
        var (state, consumed) = Run(GrantState.Owed, 100, 100, 100, 0, 105, 105, 105, 100, 20);
        Assert.Equal(GrantState.Idle, state);
        Assert.True(consumed);
    }

    [Fact]
    public void Post_turn_credit_takes_one_pull_down_to_consume()
    {
        Assert.Equal(GrantState.Pinning, ExtraTurn.Classify(5, 5));
        var (state, consumed) = Run(GrantState.Pinning, 105, 105, 105, 0);
        Assert.Equal(GrantState.Idle, state);
        Assert.True(consumed);
    }

    [Fact]
    public void A_turn_end_oscillation_does_not_fake_the_bonus_consume()
    {
        // Kill-turn end oscillates 0/105/0; the refractory keeps it ONE event -> Pinning holds.
        var (state, consumed) = Run(GrantState.Owed, 100, 100, 100, 0, 105, 0, 105, 105);
        Assert.Equal(GrantState.Pinning, state);
        Assert.False(consumed);
    }

    /// <summary>Drive Observe+Step over a CT read trace, as the orchestrator does each tick.</summary>
    private static (GrantState state, bool consumed) Run(GrantState state, params int[] ctReads)
    {
        int streak = 0;
        bool took = false, consumed = false;
        foreach (int ct in ctReads)
        {
            (streak, took, bool pullDown) = ExtraTurn.Observe(streak, took, ct);
            (state, bool c) = ExtraTurn.Step(state, pullDown);
            if (c) consumed = true;
        }
        return (state, consumed);
    }
}
