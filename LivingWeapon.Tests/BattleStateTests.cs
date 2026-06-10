using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure battle in/out state machine. Enter is immediate (sentinel arm OR a live battleMode);
/// exit is DEBOUNCED -- battleMode is a cursor-tile-class encoder (move-browsing reads 1 for seconds,
/// Paused reads 0, mid-battle dialogue reads 0 with a real eventId), and the slot9 sentinel both
/// sticks on the world map and can drop mid-battle. So exit only fires after sustained out-of-live
/// time, with pause + real-event ticks suspending the timer. Driven by synthetic DateTimes here --
/// the machine never reads the wall clock itself.
/// </summary>
public class BattleStateTests
{
    private const uint WM9 = 0xFFFFFFFFu;   // the stuck slot9 world-map sentinel
    private static DateTime T0 => new(2026, 1, 1);

    // World-map (fully out) reads: slot0=0, slot9=0, mode=0, not paused, no event.
    private static BattleEdge StepWorld(BattleState bs, DateTime now, uint slot9 = 0)
        => bs.Step(slot0: 0, slot9: slot9, battleMode: 0, paused: false, eventId: 0, now: now);

    // --- EnterSignal (pure) ---

    [Theory]
    [InlineData(0xFFu, 0xFFFFFFFFu, 0, true)]   // both sentinels armed
    [InlineData(0u, 0u, 2, true)]               // live battlefield mode 2
    [InlineData(0u, 0u, 4, true)]               // instant-targeting mode 4
    [InlineData(0xFFu, 0u, 3, true)]            // mode 3 + the in-battle marker
    [InlineData(0u, 0u, 3, false)]              // mode 3 alone is NOT a battle (menu cursor class)
    [InlineData(0u, 0u, 1, false)]              // move-browsing cursor class, not a battle
    [InlineData(0u, 0u, 0, false)]              // world map
    public void EnterSignal_only_on_sentinels_or_a_live_mode(uint slot0, uint slot9, int mode, bool expected)
        => Assert.Equal(expected, BattleState.EnterSignal(slot0, slot9, mode));

    // --- IsRealEvent (pure) boundary ---

    // OLD contract (1..399 band) was guesswork; live log on 2026-06-10 showed event 401 during
    // a mid-battle story dialogue. Real exits read eventId=0xFFFF (65535). Contract: any nonzero
    // id except 0xFFFF is a real event that suspends the exit timer.
    [Theory]
    [InlineData(0, false)]        // no event -- excluded, unknown semantics
    [InlineData(0xFFFF, false)]   // the 0xFFFF sentinel seen on real battle exits; not a real event
    [InlineData(1, true)]         // first real event id
    [InlineData(399, true)]       // previously-last real event id (still valid)
    [InlineData(400, true)]       // old band boundary -- now also a real event (live bug fix)
    [InlineData(401, true)]       // live-confirmed event id (2026-06-10 log, event during mid-battle dialogue)
    [InlineData(65000, true)]     // high id also suspends (old band excluded it, new contract includes it)
    [InlineData(302, true)]       // a story-dialogue id
    public void IsRealEvent_any_nonzero_non_sentinel_id_suspends_exit_timer(int e, bool expected)
        => Assert.Equal(expected, BattleState.IsRealEvent(e));

    // --- Enter edges ---

    [Fact]
    public void Enters_via_sentinels()
    {
        var bs = new BattleState();
        Assert.False(bs.In);
        var edge = bs.Step(0xFF, WM9, 0, paused: false, eventId: 0, now: T0);
        Assert.Equal(BattleEdge.Entered, edge);
        Assert.True(bs.In);
    }

    [Fact]
    public void Enters_via_battle_mode_2()
    {
        var bs = new BattleState();
        Assert.Equal(BattleEdge.Entered, bs.Step(0, 0, 2, false, 0, T0));
        Assert.True(bs.In);
    }

    [Fact]
    public void Enters_via_battle_mode_4()
    {
        var bs = new BattleState();
        Assert.Equal(BattleEdge.Entered, bs.Step(0, 0, 4, false, 0, T0));
        Assert.True(bs.In);
    }

    [Fact]
    public void Mode_3_alone_does_not_enter_but_mode_3_with_marker_does()
    {
        var bs = new BattleState();
        Assert.Equal(BattleEdge.None, bs.Step(0, 0, 3, false, 0, T0));   // mode 3, no marker
        Assert.False(bs.In);
        Assert.Equal(BattleEdge.Entered, bs.Step(0xFF, 0, 3, false, 0, T0));  // marker present
        Assert.True(bs.In);
    }

    [Fact]
    public void Out_stays_out_on_world_map_values()
    {
        var bs = new BattleState();
        var t = T0;
        for (int i = 0; i < 200; i++)   // ~6.6s of world map
        {
            Assert.Equal(BattleEdge.None, StepWorld(bs, t));
            Assert.False(bs.In);
            t = t.AddMilliseconds(33);
        }
    }

    // --- Exit debounce: in-live frames never accumulate ---

    [Fact]
    public void In_with_sentinel_dead_move_browsing_never_exits()
    {
        // slot0 stays 0xFF (the in-battle marker) while move-browsing reads mode 1 with slot9 dropped.
        // slot0==0xFF keeps InLiveBattle true, so the exit timer never accumulates.
        var bs = new BattleState();
        bs.Step(0xFF, WM9, 0, false, 0, T0);   // enter
        var t = T0;
        for (int i = 0; i < 200; i++)   // >6s of sentinel-dead move-browsing
        {
            t = t.AddMilliseconds(33);
            Assert.Equal(BattleEdge.None, bs.Step(0xFF, 0, 1, false, 0, t));
            Assert.True(bs.In);
        }
    }

    [Fact]
    public void In_while_paused_out_of_live_never_exits()
    {
        // Paused reads mode 0 / slot0 0 (out of live), but a paused tick is SUSPENDED -- no accumulation.
        var bs = new BattleState();
        bs.Step(0, 0, 2, false, 0, T0);   // enter via mode 2
        var t = T0;
        for (int i = 0; i < 200; i++)
        {
            t = t.AddMilliseconds(33);
            Assert.Equal(BattleEdge.None, bs.Step(0, 0, 0, paused: true, eventId: 0, now: t));
            Assert.True(bs.In);
        }
    }

    [Fact]
    public void In_with_mid_battle_dialogue_never_exits()
    {
        // Mid-battle story dialogue: out-of-live values (mode 0) but a real eventId suspends the timer.
        var bs = new BattleState();
        bs.Step(0, 0, 2, false, 0, T0);   // enter
        var t = T0;
        for (int i = 0; i < 200; i++)
        {
            t = t.AddMilliseconds(33);
            Assert.Equal(BattleEdge.None, bs.Step(0, 0, 0, paused: false, eventId: 302, now: t));
            Assert.True(bs.In);
        }
    }

    // Regression: 2026-06-10 live log -- event 401 fired mid-battle; battleMode read 0 for ~4.7s;
    // the old 1..399 band did NOT treat 401 as a real event so the exit timer ran and KillTracker
    // was reset mid-fight, losing a credited kill.
    [Fact]
    public void Mid_battle_event_401_suspends_exit_timer_and_does_not_reset_kill_tracker()
    {
        // Replay: enter (slot0=0xFF slot9=0xFFFFFFFF mode=1, using mode=2 for enter signal);
        // feed > ExitDebounceSeconds of mode=0 ticks with eventId=401 -> no Exited edge;
        // then a live tick -> still In; then mode=0 with eventId=0xFFFF (real exit sentinel)
        // sustained > debounce -> Exited fires.
        var bs = new BattleState();
        var t = T0;

        // Enter (mode=2 is the clearest enter signal matching live state "mode=1" -> debounce is
        // already in, but mode=2 fires Entered immediately per EnterSignal contract).
        var edge = bs.Step(0xFF, 0xFFFFFFFF, 2, paused: false, eventId: 0, now: t);
        Assert.Equal(BattleEdge.Entered, edge);
        Assert.True(bs.In);

        // ~4.7s of out-of-live with eventId=401 -- must NOT fire Exited.
        double elapsed = 0;
        while (elapsed < ExitDebounceSeconds_TestConst + 0.7)
        {
            t = t.AddMilliseconds(33);
            elapsed += 0.033;
            edge = bs.Step(slot0: 0x66, slot9: 0xFFFFFFFF, battleMode: 0,
                           paused: false, eventId: 401, now: t);
            Assert.NotEqual(BattleEdge.Exited, edge);
            Assert.True(bs.In);
        }

        // Live tick returns (mode=2) -> still In, accumulator cleared.
        t = t.AddMilliseconds(33);
        edge = bs.Step(0x66, 0xFFFFFFFF, battleMode: 2, paused: false, eventId: 0, now: t);
        Assert.NotEqual(BattleEdge.Exited, edge);
        Assert.True(bs.In);

        // Real exit: mode=0, eventId=0xFFFF (sentinel seen on every real exit in the log).
        // Sustain past debounce -> Exited fires.
        BattleEdge exitEdge = BattleEdge.None;
        for (int i = 0; i < 200 && exitEdge != BattleEdge.Exited; i++)
        {
            t = t.AddMilliseconds(33);
            exitEdge = bs.Step(0x66, 0xFFFFFFFF, battleMode: 0,
                               paused: false, eventId: 0xFFFF, now: t);
        }
        Assert.Equal(BattleEdge.Exited, exitEdge);
        Assert.False(bs.In);
    }

    // eventId 0 does NOT suspend -- zero has unknown semantics and the old behavior is preserved.
    [Fact]
    public void EventId_zero_does_not_suspend_exit_timer()
    {
        var bs = new BattleState();
        var t = T0;
        bs.Step(0, 0, 2, false, 0, t);   // enter
        BattleEdge exitEdge = BattleEdge.None;
        for (int i = 0; i < 200 && exitEdge != BattleEdge.Exited; i++)
        {
            t = t.AddMilliseconds(33);
            exitEdge = bs.Step(0, 0, battleMode: 0, paused: false, eventId: 0, now: t);
        }
        Assert.Equal(BattleEdge.Exited, exitEdge);
        Assert.False(bs.In);
    }

    // Ids 400 and 65000 were excluded by the old band but must now suspend.
    [Theory]
    [InlineData(400)]
    [InlineData(65000)]
    public void EventIds_above_old_band_also_suspend_exit_timer(int eventId)
    {
        var bs = new BattleState();
        var t = T0;
        bs.Step(0, 0, 2, false, 0, t);   // enter
        double elapsed = 0;
        while (elapsed < ExitDebounceSeconds_TestConst + 0.5)
        {
            t = t.AddMilliseconds(33);
            elapsed += 0.033;
            var edge = bs.Step(0, 0, battleMode: 0, paused: false, eventId: eventId, now: t);
            Assert.NotEqual(BattleEdge.Exited, edge);
            Assert.True(bs.In);
        }
    }

    // Constant mirrors BattleState.ExitDebounceSeconds to keep loop bounds correct if the value changes.
    private const double ExitDebounceSeconds_TestConst = BattleState.ExitDebounceSeconds;

    // --- Exit fires on sustained world map ---

    [Fact]
    public void Exits_after_the_debounce_on_a_sustained_stuck_sentinel_world_map()
    {
        var bs = new BattleState();
        bs.Step(0xFF, WM9, 0, false, 0, T0);   // enter
        var t = T0;
        // slot9 stays stuck 0xFFFFFFFF, slot0 drops to 0, mode 0, no pause, no event = sustained out-of-live.
        // No exit before the 4s debounce.
        while ((t - T0).TotalSeconds < 3.9)
        {
            t = t.AddMilliseconds(33);
            Assert.Equal(BattleEdge.None, StepWorld(bs, t, slot9: WM9));
            Assert.True(bs.In);
        }
        // Exit by ~4.2s.
        BattleEdge edge = BattleEdge.None;
        while ((t - T0).TotalSeconds < 4.3)
        {
            t = t.AddMilliseconds(33);
            edge = StepWorld(bs, t, slot9: WM9);
            if (edge == BattleEdge.Exited) break;
        }
        Assert.Equal(BattleEdge.Exited, edge);
        Assert.False(bs.In);
    }

    [Fact]
    public void A_single_in_live_tick_clears_the_accumulated_exit_timer()
    {
        var bs = new BattleState();
        bs.Step(0, 0, 2, false, 0, T0);   // enter
        var t = T0;
        // ~3s out of live (not yet expired)
        for (int i = 0; i < 91; i++) { t = t.AddMilliseconds(33); StepWorld(bs, t, slot9: WM9); }
        Assert.True(bs.In);
        // one in-live tick resets the accumulator
        t = t.AddMilliseconds(33);
        Assert.Equal(BattleEdge.None, bs.Step(0, 0, 2, false, 0, t));
        // ~3s more out of live -- still under 4s of CONTIGUOUS out-of-live, so still In
        for (int i = 0; i < 91; i++)
        {
            t = t.AddMilliseconds(33);
            Assert.Equal(BattleEdge.None, StepWorld(bs, t, slot9: WM9));
            Assert.True(bs.In);
        }
    }

    [Fact]
    public void Full_scenario_battle_then_stuck_world_map_exit_then_next_battle_enter()
    {
        var bs = new BattleState();
        bs.Step(0xFF, WM9, 2, false, 0, T0);   // battle 1 enter (mode 2)
        Assert.True(bs.In);
        var t = T0;
        // stuck-sentinel world map >= 4s -> Exit
        BattleEdge exit = BattleEdge.None;
        while ((t - T0).TotalSeconds < 5 && exit != BattleEdge.Exited)
        {
            t = t.AddMilliseconds(33);
            exit = StepWorld(bs, t, slot9: WM9);
        }
        Assert.Equal(BattleEdge.Exited, exit);
        Assert.False(bs.In);
        // next tick has battlefield values -> Enter
        t = t.AddMilliseconds(33);
        Assert.Equal(BattleEdge.Entered, bs.Step(0xFF, WM9, 2, false, 0, t));
        Assert.True(bs.In);
    }
}
