using System;
using System.Collections.Generic;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-184: direct unit tests for the declarative tick-phase cadence primitive (TickPhase.cs) that
/// replaced Engine's three hand-rolled counters (_tick/GrowthEveryNTicks, _ringThrottleTick,
/// _gunSlingerThrottleTick). Rows are constructed here with recording lambdas -- no Engine needed --
/// so the cadence math is pinned cheaply before EngineTests' end-to-end table pin.
/// </summary>
public class TickPhaseTests
{
    [Fact]
    public void Step_runs_only_when_the_gate_passes()
    {
        int runs = 0;
        var phase = new TickPhase("t", _ => false, 1, false, Array.Empty<string>(), _ => runs++);
        var s = new TickPhaseState();

        phase.Step(s);
        phase.Step(s);

        Assert.Equal(0, runs);
    }

    [Fact]
    public void Gate_failing_ticks_do_not_advance_the_cadence_counter()
    {
        // N=3, FiresOnFirstPass=false: pass, pass, FAIL, pass -> fires on the third PASSING tick
        // (the fourth Step call), not the third call (which was the gate-failing one).
        bool gateOpen = true;
        int runs = 0;
        var phase = new TickPhase("t", _ => gateOpen, 3, false, Array.Empty<string>(), _ => runs++);
        var s = new TickPhaseState();

        phase.Step(s);   // pass 1
        Assert.Equal(0, runs);
        phase.Step(s);   // pass 2
        Assert.Equal(0, runs);
        gateOpen = false;
        phase.Step(s);   // gate FAILS -- must not count toward the cadence
        Assert.Equal(0, runs);
        gateOpen = true;
        phase.Step(s);   // pass 3 (third PASSING tick) -- fires now
        Assert.Equal(1, runs);
    }

    [Fact]
    public void FiresOnFirstPass_true_fires_on_passes_1_then_1_plus_N()
    {
        int runs = 0;
        var phase = new TickPhase("t", _ => true, 3, true, Array.Empty<string>(), _ => runs++);
        var s = new TickPhaseState();
        var fireAtPass = new List<int>();

        for (int pass = 1; pass <= 7; pass++)
        {
            phase.Step(s);
            if (runs > fireAtPass.Count) fireAtPass.Add(pass);
        }

        Assert.Equal(new[] { 1, 4, 7 }, fireAtPass);
    }

    [Fact]
    public void FiresOnFirstPass_false_fires_on_passes_N_then_2N()
    {
        int runs = 0;
        var phase = new TickPhase("t", _ => true, 3, false, Array.Empty<string>(), _ => runs++);
        var s = new TickPhaseState();
        var fireAtPass = new List<int>();

        for (int pass = 1; pass <= 6; pass++)
        {
            phase.Step(s);
            if (runs > fireAtPass.Count) fireAtPass.Add(pass);
        }

        Assert.Equal(new[] { 3, 6 }, fireAtPass);
    }

    [Fact]
    public void EveryNTicks_1_fires_every_passing_tick()
    {
        int runs = 0;
        var phase = new TickPhase("t", _ => true, 1, false, Array.Empty<string>(), _ => runs++);
        var s = new TickPhaseState();

        phase.Step(s);
        phase.Step(s);
        phase.Step(s);

        Assert.Equal(3, runs);
    }
}
