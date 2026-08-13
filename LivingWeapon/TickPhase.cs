using System;

namespace LivingWeapon;

/// <summary>
/// The mutable per-tick blackboard Engine.Tick's prologue fills once (every field, every tick --
/// including Changed = false, since a gate-skipped kill-poll row must not leave a stale true from
/// a previous tick), then every <see cref="TickPhase"/> row reads or writes. One instance is
/// reused for the engine's whole lifetime (LW-184: replaces the locals the hand-rolled Tick body
/// used to thread through nested blocks).
/// </summary>
internal sealed class TickPhaseState
{
    public DateTime Now;
    public bool OnField;
    public bool InLive;
    public bool BattleDisplayed;
    public bool NowIn;
    public bool BattleStatus;
    public bool KitLaneArmed;

    /// <summary>Written by the kill-poll row, read by the toast and save-on-change rows --
    /// exactly why those two rows carry <c>after: ["kill-poll"]</c>.</summary>
    public bool Changed;
}

/// <summary>
/// Named gate predicates for <see cref="TickPhase"/> rows. Static readonly fields, not methods,
/// so a test can pin a row's gate by reference (<c>Assert.Same</c>) instead of re-deriving the
/// predicate's behavior.
/// </summary>
internal static class TickGates
{
    public static readonly Func<TickPhaseState, bool> Always = _ => true;
    public static readonly Func<TickPhaseState, bool> KitLane = s => s.KitLaneArmed;
    public static readonly Func<TickPhaseState, bool> InBattle = s => s.NowIn;
    public static readonly Func<TickPhaseState, bool> OutOfBattle = s => !s.NowIn;
}

/// <summary>
/// One row of Engine's declarative tick fan-out table (LW-184): a gate, a cadence, and the
/// action to run. Replaces the three hand-rolled counters (_tick/GrowthEveryNTicks,
/// _ringThrottleTick, _gunSlingerThrottleTick) with one reusable, unit-tested cadence primitive.
/// </summary>
internal sealed class TickPhase
{
    public readonly string Name;
    public readonly Func<TickPhaseState, bool> Gate;
    public readonly int EveryNTicks;
    public readonly bool FiresOnFirstPass;
    public readonly string[] After;
    public readonly Action<TickPhaseState> Run;
    private int _count;

    public TickPhase(string name, Func<TickPhaseState, bool> gate, int everyNTicks,
        bool firesOnFirstPass, string[] after, Action<TickPhaseState> run)
    {
        Name = name;
        Gate = gate;
        EveryNTicks = everyNTicks;
        FiresOnFirstPass = firesOnFirstPass;
        After = after;
        Run = run;
    }

    /// <summary>Advance and (maybe) run this row for one engine tick. The ONLY public behavior.
    /// A failing gate does nothing and the cadence counter does NOT advance, so an
    /// intermittently-open gate still fires on its Nth PASSING tick, not its Nth call -- the
    /// counter never resets on a gate miss or a battle edge, mirroring the hand-rolled counters
    /// it replaces (they only ever incremented, never reset, outside their own throttle fire).</summary>
    public void Step(TickPhaseState s)
    {
        if (!Gate(s)) return;
        if (EveryNTicks == 1) { Run(s); return; }
        if (FiresOnFirstPass)
        {
            // Reproduces `_tick++ % GrowthEveryNTicks == 0`: fires on passing tick 1, then every
            // Nth passing tick after (1, 1+N, 1+2N, ...).
            if (_count++ % EveryNTicks == 0) Run(s);
        }
        else
        {
            // Reproduces `++_gunSlingerThrottleTick >= N` (and the old ring-throttle counter):
            // first fire on the Nth passing tick, then every Nth after, counter reset on fire.
            if (++_count >= EveryNTicks) { _count = 0; Run(s); }
        }
    }
}
