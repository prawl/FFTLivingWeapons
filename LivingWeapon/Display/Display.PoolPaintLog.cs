namespace LivingWeapon;

/// <summary>
/// Pool-paint log rate-limit gates, split out of Display.PoolPaint.cs when it crossed the 200-line trigger (155 to 217 lines).
/// Sits beside Display.Flight.cs; this file emits plain ModLogger.Debug lines, not Flight.Record taps. MaybePoolPaint reads two gates.
/// - NoPoolRegionLogGate / ReArmNoPoolRegionLog: Signatures.StuckEdge over "no named-pool region located"; fires on entry,
///   silent while stuck, re-arms once a region is found.
/// - PoolCoverageLogGate: fires when _poolCovered differs from what this gate last reported (either direction) or on the first call.
///   It is not StuckEdge, which only detects one direction.
/// </summary>
internal sealed partial class Display
{
    private bool _noPoolRegionLatched;
    private bool _poolCoverageEverLogged;
    private bool _lastLoggedPoolCoverage;

    /// <summary>True (and marks logged) on the false-&gt;true edge into "no region found". Call once per MaybePoolPaint pass that finds zero regions.</summary>
    private bool NoPoolRegionLogGate() => Signatures.StuckEdge(ref _noPoolRegionLatched, true);

    /// <summary>Re-arms the no-region gate once a region is found again, so the next drought logs its own line.</summary>
    private void ReArmNoPoolRegionLog() => _noPoolRegionLatched = false;

    /// <summary>True (and marks logged) when <paramref name="covered"/> differs from what this gate last reported, in either direction,
    /// or has never reported, so a repeated same-state result logs once rather than every Tick.
    ///
    /// Repeated fall-through comes from Engine.cs's Display.Invalidate() on every status-card open (Engine.cs:525), which sets
    /// _poolCovered=false and queues a PoolLocator restart (PoolLocator.cs), so the next several Ticks re-enter MaybePoolPaint's
    /// fall-through branch until coverage re-latches. Open/close cycling would otherwise re-log this summary and pay its
    /// _sites.Snapshot() cost every tick. The CardEvictStrikes incident (Tuning.cs) is not the trigger: the site count fell 836 to
    /// 726 to 690 while CoversAllMeta() stayed true, so _poolCovered never dropped and the branch was never reached. The other risk is
    /// ScanPoolRegion's documented one, a tracked weapon absent from every pool region so coverage never latches.</summary>
    private bool PoolCoverageLogGate(bool covered)
    {
        bool changed = !_poolCoverageEverLogged || covered != _lastLoggedPoolCoverage;
        if (changed) { _poolCoverageEverLogged = true; _lastLoggedPoolCoverage = covered; }
        return changed;
    }
}
