namespace LivingWeapon;

/// <summary>
/// LW-257 (round-4 review, item 3): the pool-paint logging/rate-limit apparatus, split out of
/// Display.PoolPaint.cs once that file was measured to have crossed the 200-line trigger
/// silently (155 to 217 lines) with the new observability work this arc added -- CardSites.cs
/// got an explicit line-count justification when it crossed the same trigger; this file is the
/// seam that makes the same justification unnecessary here, since there was a real one to draw.
/// Sits beside Display.Flight.cs (the flight-recorder tap's own home) since both are "how do we
/// watch this system without flooding the log/tape" concerns, even though this file's output is
/// plain ModLogger.Debug lines, not Flight.Record taps -- a genuinely separate channel, which is
/// why this is its own file rather than folded into that one.
///
/// Two independent gates, each a small state machine MaybePoolPaint (Display.PoolPaint.cs) reads
/// through a single call:
/// - NoPoolRegionLogGate / ReArmNoPoolRegionLog: Signatures.StuckEdge over "no named-pool region
///   located" (item 7) -- fires once on the transition into that state, silent while stuck,
///   re-arms once a region is found again.
/// - PoolCoverageLogGate: fires once whenever _poolCovered differs from what THIS gate last
///   reported (either direction), or on the very first call. Deliberately NOT
///   Signatures.StuckEdge (which only detects one direction): the transition INTO coverage is
///   worth its own line here too, not just AnnounceCoverage's separate Info/Debug pair.
/// </summary>
internal sealed partial class Display
{
    private bool _noPoolRegionLatched;
    private bool _poolCoverageEverLogged;
    private bool _lastLoggedPoolCoverage;

    /// <summary>True (and marks logged) exactly on the false-&gt;true edge into "no region
    /// found". Call once per MaybePoolPaint pass that finds zero regions.</summary>
    private bool NoPoolRegionLogGate() => Signatures.StuckEdge(ref _noPoolRegionLatched, true);

    /// <summary>Re-arms the no-region gate once a region IS found again, so the next drought
    /// gets its own transition line.</summary>
    private void ReArmNoPoolRegionLog() => _noPoolRegionLatched = false;

    /// <summary>True (and marks logged) whenever <paramref name="covered"/> differs from what
    /// this gate last reported, in EITHER direction, or has never reported at all -- so a
    /// persistent SAME-state result logs once, not every Tick.
    ///
    /// The real trigger for repeated fall-through (round-4 review, item 4 -- corrected from an
    /// earlier draft that cited the WRONG incident): Engine.cs's Display.Invalidate() call on
    /// every status-card open (Engine.cs:525) sets _poolCovered=false and (LW-261: queues a
    /// PoolLocator restart rather than clearing its cache outright, PoolLocator.cs's own class
    /// doc) marks PoolLocator's cache stale, so the next several Ticks genuinely re-enter
    /// MaybePoolPaint's fall-through branch until coverage re-latches -- repeated status-card
    /// open/close cycling could otherwise re-log this summary (and pay its _sites.Snapshot() cost)
    /// every one of those ticks. NOT the CardEvictStrikes-tuned incident (Tuning.cs's own doc): that incident's site
    /// count fell 836 to 726 to 690 while CoversAllMeta() stayed TRUE throughout, so _poolCovered
    /// never dropped and this fall-through branch was never reached during it at all --
    /// MaybePoolPaint's top short-circuit kept returning early via the count-compare/
    /// CoversAllMeta re-check the whole time. The OTHER real risk this gate protects against is
    /// ScanPoolRegion's own documented one: a tracked weapon absent from every pool region, where
    /// coverage never latches at all.</summary>
    private bool PoolCoverageLogGate(bool covered)
    {
        bool changed = !_poolCoverageEverLogged || covered != _lastLoggedPoolCoverage;
        if (changed) { _poolCoverageEverLogged = true; _lastLoggedPoolCoverage = covered; }
        return changed;
    }
}
