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

    [Theory]
    [InlineData(0, false)]        // no event
    [InlineData(0xFFFF, false)]   // the nameId-alias sentinel, not a real event
    [InlineData(1, true)]         // first real event id
    [InlineData(399, true)]       // last real event id
    [InlineData(400, false)]      // just past the band
    [InlineData(302, true)]       // a story-dialogue id
    public void IsRealEvent_band_is_1_to_399_excluding_nameId_alias(int e, bool expected)
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
