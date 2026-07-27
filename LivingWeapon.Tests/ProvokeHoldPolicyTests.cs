using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Pure-decision coverage for the Provoke hold (LW-123 arc 2a): release priority, the WINDOW-mode
/// hide/reveal choice, the turn-edge test, the watchdog accumulator, and the guarded status-bit
/// writers exercised for real against a PinnedBuf via LiveMemory (the FeignDeath.Policy.cs /
/// FeignDeathTests.cs precedent). Module-level (band-seat) coverage lives in ProvokeHoldTests.cs.
/// </summary>
public class ProvokeHoldPolicyTests
{
    private static readonly LiveMemory Live = new();

    // ---- ActionFor (LW-127, D1 revised: the six-branch hide/reveal rule) ----
    //
    // Branch order, first match wins. RE-ORDERED after the 2026-07-27 live pass failed both casts
    // (a player-side seat's own turn was revealing the party even while the marked enemy was still
    // genuinely next to act, and its turn opened against a fully visible party the instant the
    // player's turn ended -- see ProvokeHoldTests.Marked_enemy_next_stays_hidden_even_on_a_player_turn_live_2026_07_27),
    // THEN GAINED THE ENGAGEMENT LATCH after cast 3's follow-up failure (CT is paid at turn START, so
    // TryEta misreads the marked enemy's own CT as "far off" for a few ticks right as its turn opens
    // -- see ProvokeHoldTests.Marked_enemys_own_ct_payment_does_not_reveal_the_party_live_2026_07_27_cast3):
    // (0) engaged -> Hide, (1) markedIsActor -> Hide, (2) markedIsNext -> Hide,
    // (3) playerSideOwnsTurn -> Reveal, (4) markedIsFarOff -> Reveal, (5) default -> Hide. Each test
    // below isolates one branch by setting every earlier-priority input false; the priority tests pin
    // that an earlier branch wins even when a later one would also fire -- in particular, engaged now
    // beats every other signal (STATE beats every ranking read), and markedIsActor/markedIsNext beat
    // playerSideOwnsTurn, the exact reordering the live fixes required. The arrangement that broke the
    // OLD (pre-LW-127) version of this function live is pinned at module level (ProvokeHoldTests, the
    // WindowMode cursor tests), because that bug was in what got READ, which a pure function cannot
    // express.

    [Fact]
    public void ActionFor_reveals_while_a_player_side_seat_owns_the_turn()
        => Assert.Equal(ProvokeHold.HideAction.Reveal, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: true, markedIsActor: false, markedIsNext: false, markedIsFarOff: false));

    /// <summary>THE CAST-3 LIVE FIX, pinned at the pure-function level: engaged beats every other
    /// signal, including a player-side seat owning the turn and the marked enemy reading far off --
    /// the exact misprediction the engagement latch exists to survive.</summary>
    [Fact]
    public void ActionFor_engaged_beats_every_other_signal()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(engaged: true,
            playerSideOwnsTurn: true, markedIsActor: false, markedIsNext: false, markedIsFarOff: true));

    /// <summary>THE CAST-2 LIVE FIX, pinned at the pure-function level: markedIsActor now beats
    /// playerSideOwnsTurn (moved from branch 2 to branch 1).</summary>
    [Fact]
    public void ActionFor_marked_is_actor_beats_player_side_turn()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: true, markedIsActor: true, markedIsNext: false, markedIsFarOff: false));

    /// <summary>THE CAST-2 LIVE FIX, pinned at the pure-function level: markedIsNext now beats
    /// playerSideOwnsTurn (moved from branch 3 to branch 2) -- this is the exact reordering the
    /// 2026-07-27 live failure demanded (nextNameId matched the marked enemy, markedEta ==
    /// leaderEta, and the OLD order still revealed the party because a player-side seat owned the
    /// turn).</summary>
    [Fact]
    public void ActionFor_marked_is_next_beats_player_side_turn()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: true, markedIsActor: false, markedIsNext: true, markedIsFarOff: false));

    [Fact]
    public void ActionFor_hides_when_the_marked_enemy_is_the_current_actor()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: false, markedIsActor: true, markedIsNext: false, markedIsFarOff: true));

    [Fact]
    public void ActionFor_hides_when_the_marked_enemy_is_next_to_act()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: false, markedIsActor: false, markedIsNext: true, markedIsFarOff: false));

    [Fact]
    public void ActionFor_reveals_when_the_marked_enemy_is_clearly_further_off()
        => Assert.Equal(ProvokeHold.HideAction.Reveal, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: false, markedIsActor: false, markedIsNext: false, markedIsFarOff: true));

    /// <summary>Branch 5, the DEFAULT (not merely the unreadable-read case): bias-to-hidden survives
    /// D1's rewrite, so an unusable CT/Speed read, a close call under the margin, or no enemy
    /// candidate at all all land here.</summary>
    [Fact]
    public void ActionFor_hides_by_default_when_nothing_else_applies()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(engaged: false,
            playerSideOwnsTurn: false, markedIsActor: false, markedIsNext: false, markedIsFarOff: false));

    // ---- ReleaseReason: each reason isolated ----

    private static ProvokeHold.Release Reason(bool bearerPresent = true, bool bearerAlive = true,
        bool markedLocated = true, bool markedDead = false, bool markedMissedOut = false,
        bool markedDisabled = false, int markedTurns = 0, int provokeTurns = 1, bool watchdogElapsed = false)
        => ProvokeHold.ReleaseReason(bearerPresent, bearerAlive, markedLocated, markedDead, markedMissedOut,
            markedDisabled, markedTurns, provokeTurns, watchdogElapsed);

    [Fact]
    public void ReleaseReason_none_when_armed_and_nothing_fired()
        => Assert.Equal(ProvokeHold.Release.None, Reason());

    [Fact]
    public void ReleaseReason_bearer_gone()
        => Assert.Equal(ProvokeHold.Release.BearerGone, Reason(bearerPresent: false));

    [Fact]
    public void ReleaseReason_bearer_dead()
        => Assert.Equal(ProvokeHold.Release.BearerDead, Reason(bearerAlive: false));

    [Fact]
    public void ReleaseReason_enemy_dead()
        => Assert.Equal(ProvokeHold.Release.EnemyDead, Reason(markedLocated: true, markedDead: true));

    [Fact]
    public void ReleaseReason_enemy_gone()
        => Assert.Equal(ProvokeHold.Release.EnemyGone, Reason(markedLocated: false, markedMissedOut: true));

    [Fact]
    public void ReleaseReason_enemy_disabled()
        => Assert.Equal(ProvokeHold.Release.EnemyDisabled, Reason(markedLocated: true, markedDisabled: true));

    [Fact]
    public void ReleaseReason_enemy_turn_done()
        => Assert.Equal(ProvokeHold.Release.EnemyTurnDone, Reason(markedTurns: 1, provokeTurns: 1));

    [Fact]
    public void ReleaseReason_watchdog()
        => Assert.Equal(ProvokeHold.Release.Watchdog, Reason(watchdogElapsed: true));

    // ---- ReleaseReason: priority order ----

    [Fact]
    public void ReleaseReason_bearer_gone_beats_enemy_turn_done()
        => Assert.Equal(ProvokeHold.Release.BearerGone, Reason(bearerPresent: false, markedTurns: 5, provokeTurns: 1));

    [Fact]
    public void ReleaseReason_enemy_dead_beats_turn_done()
        => Assert.Equal(ProvokeHold.Release.EnemyDead,
            Reason(markedLocated: true, markedDead: true, markedTurns: 5, provokeTurns: 1));

    [Fact]
    public void ReleaseReason_a_real_reason_beats_watchdog_when_both_true()
        => Assert.Equal(ProvokeHold.Release.EnemyTurnDone,
            Reason(markedTurns: 1, provokeTurns: 1, watchdogElapsed: true));

    [Fact]
    public void ReleaseReason_bearer_dead_beats_watchdog_too()
        => Assert.Equal(ProvokeHold.Release.BearerDead, Reason(bearerAlive: false, watchdogElapsed: true));

    // ---- TurnEnded ----

    [Fact]
    public void TurnEnded_falling_edge_true()
        => Assert.True(ProvokeHold.TurnEnded(wasActive: true, nowActive: false));

    [Fact]
    public void TurnEnded_rising_edge_is_not_an_end()
        => Assert.False(ProvokeHold.TurnEnded(wasActive: false, nowActive: true));

    [Fact]
    public void TurnEnded_steady_active_is_not_an_end()
        => Assert.False(ProvokeHold.TurnEnded(wasActive: true, nowActive: true));

    [Fact]
    public void TurnEnded_steady_inactive_is_not_an_end()
        => Assert.False(ProvokeHold.TurnEnded(wasActive: false, nowActive: false));

    // ---- watchdog accumulation (unpaused-only) boundary ----

    [Fact]
    public void AccrueWatchdog_adds_the_delta_when_unpaused()
        => Assert.Equal(15.0, ProvokeHold.AccrueWatchdog(liveElapsed: 10.0, deltaSeconds: 5.0, paused: false));

    [Fact]
    public void AccrueWatchdog_ignores_the_delta_when_paused()
        => Assert.Equal(10.0, ProvokeHold.AccrueWatchdog(liveElapsed: 10.0, deltaSeconds: 5.0, paused: true));

    [Fact]
    public void WatchdogElapsed_boundary()
    {
        Assert.False(ProvokeHold.WatchdogElapsed(liveElapsed: 29.9, capSeconds: 30.0));
        Assert.True(ProvokeHold.WatchdogElapsed(liveElapsed: 30.0, capSeconds: 30.0));
        Assert.True(ProvokeHold.WatchdogElapsed(liveElapsed: 40.0, capSeconds: 30.0));
    }

    // ---- guarded writers against a real PinnedBuf (the RPM/WPM guard path runs) ----

    [Fact]
    public void SetInvisible_sets_then_clears_preserving_other_bits()
    {
        using var e = PinnedBuf.Of(256);
        e.Bytes[Offsets.AInvisible] = 0x20;   // Reraise already set on this byte
        Assert.False(ProvokeHold.HasInvisible(Live, e.Addr));

        Assert.True(ProvokeHold.SetInvisible(Live, e.Addr, on: true));
        Assert.True(ProvokeHold.HasInvisible(Live, e.Addr));
        Assert.Equal(0x30, e.Bytes[Offsets.AInvisible]);   // 0x10 invisible | 0x20 preserved

        Assert.True(ProvokeHold.SetInvisible(Live, e.Addr, on: false));
        Assert.False(ProvokeHold.HasInvisible(Live, e.Addr));
        Assert.Equal(0x20, e.Bytes[Offsets.AInvisible]);
    }

    [Fact]
    public void ClearMark_clears_both_layers_leaving_dead_and_undead_bits_untouched()
    {
        using var e = PinnedBuf.Of(600);   // StatusApply.Inflicted (0x1D3 = 467) needs more than the usual 256
        // Composed +0x45 shares its byte with Dead (0x20) / Undead (0x10); mark bit is 0x80 (id 0).
        e.Bytes[StatusApply.Composed] = (byte)(0x80 | Offsets.ADeadBit | Offsets.AUndeadBit);
        e.Bytes[StatusApply.Inflicted] = 0x80;

        Assert.True(ProvokeHold.ClearMark(Live, e.Addr));

        Assert.Equal((byte)(Offsets.ADeadBit | Offsets.AUndeadBit), e.Bytes[StatusApply.Composed]);
        Assert.Equal(0, e.Bytes[StatusApply.Inflicted]);
    }

    [Fact]
    public void SetInvisible_reports_refusal_when_the_page_is_unwritable()
    {
        var mem = new FakeSparseMemory();   // Writable() is false for every address by default
        Assert.False(ProvokeHold.SetInvisible(mem, 0x1000, on: true));
        Assert.Empty(mem.Written);
    }

    [Fact]
    public void SetInvisible_no_op_when_already_at_the_wanted_state_even_if_unwritable()
    {
        var mem = new FakeSparseMemory();
        mem.U8s[0x1000 + Offsets.AInvisible] = Offsets.AInvisibleBit;   // already set
        Assert.True(ProvokeHold.SetInvisible(mem, 0x1000, on: true));   // no write needed -> true despite no Writable
        Assert.Empty(mem.Written);
    }
}
