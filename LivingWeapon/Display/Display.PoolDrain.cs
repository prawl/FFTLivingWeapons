using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-257 commit 2. Per-region drain check. MaybePoolPaint's aggregate short-circuit
/// (Display.PoolPaint.cs) re-latches at whatever smaller count survives a partial loss, so if the
/// lost copy is the one the card renders from, nothing repaints it (which region that is is not
/// ledger-proven, see PoolLocator.cs PREMISE STATUS). <see cref="LatchRegionCounts"/> records a
/// kills-site count per region when coverage last latched. <see cref="ReOfferDrainedRegions"/>,
/// called only from RunMaintenance (Display.Heartbeat.cs), re-offers the regions that fell below
/// it through ScanPoolRegion, never a full LocateAll, Regions() walk or heap sweep.
///
/// After a re-offer, coverage is re-decided by CoversAllMeta(), not re-latched. Re-latching
/// unconditionally freezes _poolCovered=true when a whole region has relocated, which suppresses
/// MaybePoolPaint's full-relocate fallback (it runs only while _poolCovered is false). Caught by
/// PoolPaint_drained_pool_relocates_without_any_invalidate and
/// PoolPaint_steady_state_short_circuits_and_relatches_after_heal.
///
/// Clearing _poolCovered only schedules the relocate. MaybePoolPaint runs right after only when
/// RunMaintenance was reached from Tick. Via PaintCountsIfChanged (Display.Heartbeat.cs) the flag
/// waits, with drain checks off, until Engine's paint phase takes the Tick(true) branch
/// (BattleState.ShouldPaintCard, Engine.cs).
/// </summary>
internal sealed partial class Display
{
    /// <summary>Per-region kills-site count latched when overall coverage last re-latched via a
    /// real locate+scan (MaybePoolPaint in Display.PoolPaint.cs). Its aggregate fast-path re-latch
    /// leaves this alone, otherwise a drain would re-latch itself away before
    /// ReOfferDrainedRegions saw it. Keyed by the region's (base,size), which PoolLocator returns
    /// unchanged from its cache (PoolLocator.CachedRegions) until the region relocates.</summary>
    private readonly Dictionary<(long baseAddr, long size), int> _regionCountAtCoverage = new();

    private void LatchRegionCounts(IReadOnlyList<(long baseAddr, long size)> regions)
    {
        _regionCountAtCoverage.Clear();
        var snapshot = _sites.Snapshot();
        foreach (var region in regions)
            _regionCountAtCoverage[region] = CountKillsSitesIn(snapshot, region.baseAddr, region.size);
    }

    // Internal for the LW-260 pin. DisplayHeartbeatTests checks that a site at rbase+rsize is
    // outside the region (same test-accessor style as PoolLocator.Restart.cs's ProgressLogEveryTicks).
    internal static int CountKillsSitesIn(List<CardSites.Site> snapshot, long rbase, long rsize)
    {
        int count = 0;
        foreach (var s in snapshot)
            if (s.IsKills && s.SlotAddr >= rbase && s.SlotAddr < rbase + rsize) count++;
        return count;
    }

    /// <summary>Heals the gap in MaybePoolPaint's aggregate re-latch. Losing one copy of a
    /// still-covered weapon (CoversAllMeta stays true) just re-latches at the smaller count.
    /// Re-offers only the regions whose live kills-site count fell below their
    /// <see cref="_regionCountAtCoverage"/> latch, via ScanPoolRegion. Gated by
    /// Tuning.CardReOfferEnabled as a kill switch. See the class doc for why coverage is
    /// re-decided afterwards.</summary>
    private void ReOfferDrainedRegions()
    {
        if (!Tuning.CardReOfferEnabled || _regionCountAtCoverage.Count == 0) return;

        var snapshot = _sites.Snapshot();
        List<(long baseAddr, long size)>? drained = null;
        foreach (var kv in _regionCountAtCoverage)
            if (CountKillsSitesIn(snapshot, kv.Key.baseAddr, kv.Key.size) < kv.Value)
                (drained ??= new List<(long, long)>()).Add(kv.Key);
        if (drained == null) return;

        foreach (var (rbase, rsize) in drained) ScanPoolRegion(rbase, rsize);

        // Re-decide coverage instead of assuming the re-offer found everything (class doc). A
        // tracked id that lost its last copy (region relocated or vanished) must drop _poolCovered
        // so MaybePoolPaint can run the real relocate (PoolLocator.Step via
        // Display.PoolLocate.cs's StepPoolLocate). From PaintCountsIfChanged the flag waits for the
        // next Tick.
        if (!CoversAllMeta()) { _poolCovered = false; return; }

        LatchRegionCounts(new List<(long baseAddr, long size)>(_regionCountAtCoverage.Keys));
        _countAtCoverage = _sites.Count;
    }
}
