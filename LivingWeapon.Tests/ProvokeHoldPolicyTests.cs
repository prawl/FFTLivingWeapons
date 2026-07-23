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

    // ---- ActionFor (WINDOW mode's hide/reveal choice) ----

    [Fact]
    public void ActionFor_sane_player_turn_reveals()
        => Assert.Equal(ProvokeHold.HideAction.Reveal, ProvokeHold.ActionFor(queueSane: true, team: 0));

    [Fact]
    public void ActionFor_sane_ally_turn_reveals()
        => Assert.Equal(ProvokeHold.HideAction.Reveal, ProvokeHold.ActionFor(queueSane: true, team: 2));

    [Fact]
    public void ActionFor_sane_enemy_turn_hides()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(queueSane: true, team: 1));

    [Fact]
    public void ActionFor_sane_unknown_team_hides()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(queueSane: true, team: 3));

    [Fact]
    public void ActionFor_insane_queue_biases_hidden_even_on_a_player_looking_team()
        => Assert.Equal(ProvokeHold.HideAction.Hide, ProvokeHold.ActionFor(queueSane: false, team: 0));

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
