using System;
using System.Collections.Generic;
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

/// <summary>
/// ExtraTurn Tick integration: the shared-locator path (Wielder.TryResolve + Wielder.Locate)
/// threaded through IGameMemory. Tests that were blocked before the fork was deleted:
///   - Zwill in the off-hand arms correctly (the fork hard-read RRHand only).
///   - Identical-twin tie-break fires (the fork lacked it -- extra turns silently failed when
///     the battle placed the wielder on tile (0,0)).
///   - GateLost fires and restores CT when the Zwill leaves the wielder's hands.
/// All tests use FakeMemory + a pinned kill tally so no game memory is needed.
/// </summary>
public class ExtraTurnIntegrationTests
{
    private sealed class FakeMemory : IGameMemory
    {
        public readonly Dictionary<long, ushort> U16s = new();
        public readonly Dictionary<long, byte>   U8s  = new();
        public readonly HashSet<long> WritableAddrs   = new();
        public readonly Dictionary<long, byte>   Written = new();

        public byte   U8(long a)  => U8s.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;
        public bool   Writable(long a, int n) => WritableAddrs.Contains(a);
        public void   W8(long a, byte v) { Written[a] = v; U8s[a] = v; }
    }

    private static void SeatRoster(FakeMemory m, int slot, int lvl, int br, int fa,
                                    int rh, int oh = 0xFFFF)
    {
        long rb = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
        m.U8s[rb + Offsets.RLevel] = (byte)lvl;
        m.U8s[rb + Offsets.RBrave] = (byte)br;
        m.U8s[rb + Offsets.RFaith] = (byte)fa;
        m.U16s[rb + Offsets.RRHand]   = (ushort)rh;
        m.U16s[rb + Offsets.RLHand]   = 0xFFFF;
        m.U16s[rb + Offsets.ROffHand] = (ushort)oh;
    }

    private static void SeatBand(FakeMemory m, int bandIdx, int weapon, int lvl, int br, int fa,
                                  int gx, int gy, int hp = 100, int maxHp = 100,
                                  int ctTurn = 0)
    {
        long e = Band.Entry(bandIdx);
        m.U16s[e + (Offsets.CWeapon - Offsets.BandEntry)] = (ushort)weapon;
        m.U8s[e + Offsets.ALevel]  = (byte)lvl;
        m.U8s[e + Offsets.ABrave]  = (byte)br;
        m.U8s[e + Offsets.AFaith]  = (byte)fa;
        m.U8s[e + Offsets.AGx]     = (byte)gx;
        m.U8s[e + Offsets.AGy]     = (byte)gy;
        m.U16s[e + Offsets.AHp]    = (ushort)hp;
        m.U16s[e + Offsets.AMaxHp] = (ushort)maxHp;
        m.U8s[e + Offsets.ACtTurn] = (byte)ctTurn;
        // mark the CtSlam offset as writable so the slam guard passes
        m.WritableAddrs.Add(e + ExtraTurn.CtOff);
    }

    private static ExtraTurn MakeExtra(FakeMemory mem, int kills = 0)
    {
        var dict = new Dictionary<int, int>();
        if (kills > 0) dict[ExtraTurn.ZwillId] = kills;
        return new ExtraTurn(dict, mem);
    }

    // ---- Zwill in the off-hand: signatures fire from the main hand only ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    [Fact]
    public void Does_not_arm_when_zwill_is_in_the_offhand_only()
    {
        var m = new FakeMemory();
        // RRHand = some rod (id 56), ROffHand = ZwillId -- offhand does not activate the grant
        SeatRoster(m, 0, lvl: 30, br: 65, fa: 58, rh: 56, oh: ExtraTurn.ZwillId);
        SeatBand(m, 15, weapon: 56, lvl: 30, br: 65, fa: 58, gx: 4, gy: 3);

        var kills = new Dictionary<int, int> { [ExtraTurn.ZwillId] = Tuning.KillThresholds[ExtraTurn.AtTier - 1] };
        var extra = new ExtraTurn(kills, m);

        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Idle, extra.State);

        kills[ExtraTurn.ZwillId]++;
        extra.Tick(DateTime.Now);
        // Offhand Zwill: TryResolveMainHand returns false -> gate lost -> stays Idle, no CT slam
        Assert.Equal(GrantState.Idle, extra.State);
    }

    // ---- Identical-twin tie-break (missing from the old fork) ----

    [Fact]
    public void Locates_wielder_when_both_twins_sit_at_origin()
    {
        var m = new FakeMemory();
        SeatRoster(m, 0, lvl: 30, br: 89, fa: 76, rh: ExtraTurn.ZwillId);
        // Two identical entries at (0,0): the corner-tile twin scenario that broke the old fork.
        SeatBand(m, 25, weapon: ExtraTurn.ZwillId, lvl: 30, br: 89, fa: 76, gx: 0, gy: 0);
        SeatBand(m, 28, weapon: ExtraTurn.ZwillId, lvl: 30, br: 89, fa: 76, gx: 0, gy: 0);

        int threshold = Tuning.KillThresholds[ExtraTurn.AtTier - 1];
        var kills = new Dictionary<int, int> { [ExtraTurn.ZwillId] = threshold };
        var extra = new ExtraTurn(kills, m);

        // Tick 1: establishes _lastCount = threshold (no fresh kill yet since lastCount was -1).
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Idle, extra.State);

        // Tick 2: a new kill arrives -> arms the grant.
        kills[ExtraTurn.ZwillId] = threshold + 1;
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Arming, extra.State);

        // Tick 3: Wielder.Locate fires; the twin tie-break must return a non-zero entry.
        // A locate failure leaves _base == 0 (the old fork's bug). Success leaves it non-zero.
        extra.Tick(DateTime.Now);
        Assert.NotEqual(0, extra.LocatedBase);
    }

    // ---- GateLost restores CT ----

    [Fact]
    public void GateLost_restores_ct_when_zwill_leaves_the_wielder_hands()
    {
        var m = new FakeMemory();
        SeatRoster(m, 0, lvl: 30, br: 65, fa: 58, rh: ExtraTurn.ZwillId);
        SeatBand(m, 15, weapon: ExtraTurn.ZwillId, lvl: 30, br: 65, fa: 58, gx: 4, gy: 3);

        int threshold = Tuning.KillThresholds[ExtraTurn.AtTier - 1];
        var kills = new Dictionary<int, int> { [ExtraTurn.ZwillId] = threshold };
        var extra = new ExtraTurn(kills, m);

        // Tick 1: establish baseline kill count (_lastCount = threshold, state stays Idle).
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Idle, extra.State);

        // Tick 2: increment kills -> fresh kill -> arms the grant.
        kills[ExtraTurn.ZwillId] = threshold + 1;
        extra.Tick(DateTime.Now);
        Assert.Equal(GrantState.Arming, extra.State);

        // Tick 3: locate fires and _base is set; write CT=SlamCt happens on Owed/Pinning.
        // First force locate by ticking once more.
        extra.Tick(DateTime.Now);

        // Now drop the Zwill: TierFor fails -> GateLost -> Release writes CT=0.
        kills.Remove(ExtraTurn.ZwillId);
        m.U16s[Offsets.RosterBase + Offsets.RRHand] = 56;  // rod, not Zwill
        extra.Tick(DateTime.Now);

        Assert.Equal(GrantState.Idle, extra.State);
        // Release(GateLost) must have written CT=0 to the located entry.
        Assert.True(m.Written.ContainsKey(Band.Entry(15) + ExtraTurn.CtOff),
            "expected CT restore write on GateLost");
        Assert.Equal(0, m.Written[Band.Entry(15) + ExtraTurn.CtOff]);
    }
}
