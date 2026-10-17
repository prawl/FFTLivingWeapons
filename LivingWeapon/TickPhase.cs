using System;

namespace LivingWeapon;

/// <summary>
/// Per-tick blackboard filled once by Engine.Tick's prologue (every field, including Changed, so a
/// gate-skipped kill-poll row cannot leave a stale true) and read or written by each
/// <see cref="TickPhase"/> row. Reused for the engine's lifetime.
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

    /// <summary>Written by the kill-poll row, read by the toast and save-on-change rows (<c>after: ["kill-poll"]</c>).</summary>
    public bool Changed;
}

/// <summary>
/// Named gate predicates for <see cref="TickPhase"/> rows. Static fields so a test can pin a gate with <c>Assert.Same</c>.
/// </summary>
internal static class TickGates
{
    public static readonly Func<TickPhaseState, bool> Always = _ => true;
    public static readonly Func<TickPhaseState, bool> KitLane = s => s.KitLaneArmed;
    public static readonly Func<TickPhaseState, bool> InBattle = s => s.NowIn;
    public static readonly Func<TickPhaseState, bool> OutOfBattle = s => !s.NowIn;
}

/// <summary>
/// One row of Engine's declarative tick fan-out table, made of a gate, a cadence and an action.
/// Replaces the _tick/GrowthEveryNTicks, _ringThrottleTick and _gunSlingerThrottleTick counters.
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

    /// <summary>Advance and maybe run this row for one tick. A failing gate does nothing and does not
    /// advance the counter, so the row fires on its Nth passing tick. The counter never resets on a
    /// gate miss or battle edge, same as the old counters.</summary>
    public void Step(TickPhaseState s)
    {
        if (!Gate(s)) return;
        if (EveryNTicks == 1) { Run(s); return; }
        if (FiresOnFirstPass)
        {
            // Matches the old `_tick++ % GrowthEveryNTicks == 0`, firing on passing ticks 1, 1+N, 1+2N.
            if (_count++ % EveryNTicks == 0) Run(s);
        }
        else
        {
            // Matches the old `++_gunSlingerThrottleTick >= N`, firing on the Nth passing tick and resetting.
            if (++_count >= EveryNTicks) { _count = 0; Run(s); }
        }
    }
}
