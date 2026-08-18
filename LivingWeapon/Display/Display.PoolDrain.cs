using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-257 commit 2: the per-region drain check. MaybePoolPaint's own cheap aggregate short-
/// circuit (Display.PoolPaint.cs) treats "still covers every id" as good enough and re-latches at
/// whatever smaller count survives a partial loss -- it never looks again at WHICH region lost a
/// copy, so if the copy that vanished happened to be the one the card actually renders from,
/// nothing ever repaints it (PoolLocator.cs's own PREMISE STATUS doc: which located region that
/// is is not itself ledger-proven). This file closes that gap: <see cref="LatchRegionCounts"/>
/// records a KILLS-site count per region at the moment overall coverage last latched, and <see
/// cref="ReOfferDrainedRegions"/> -- called ONLY from RunMaintenance (Display.Heartbeat.cs), i.e.
/// on the maintenance beat, NEVER every tick and never from MaybePoolPaint directly -- re-offers
/// only the region(s) whose live count has fallen below that latch, via the existing
/// ScanPoolRegion. Never a full LocateAll, never a Regions() walk, never the whole-heap sweep: the
/// blast radius stays exactly the region(s) that actually look short.
///
/// THE LESSON THIS FILE WAS BUILT AROUND (caught live, against two PRE-EXISTING tests --
/// PoolPaint_drained_pool_relocates_without_any_invalidate and
/// PoolPaint_steady_state_short_circuits_and_relatches_after_heal -- not new ones written for this
/// arc): after a targeted re-offer, coverage is RE-DECIDED by CoversAllMeta(), never
/// optimistically re-latched. A first version re-latched unconditionally on the assumption that
/// "we just re-scanned, so we're caught up." But when a whole region is genuinely gone (relocated
/// to a fresh base, not just missing one redundant copy), the re-offer finds nothing there, and
/// blindly re-latching to the new, smaller, still-short count freezes _poolCovered=true at a state
/// that is actually UNCOVERED -- which permanently suppresses MaybePoolPaint's own
/// CoversAllMeta()-gated fallback (the full relocate that would have found the region's fresh
/// replacement address), because that fallback only ever runs when _poolCovered reads false. So
/// ReOfferDrainedRegions checks CoversAllMeta() itself, after the re-offer, every time: still
/// covered, it re-latches normally; still short, it sets _poolCovered = false and returns.
///
/// THAT FLIP ONLY SCHEDULES THE REAL RELOCATE, IT DOES NOT GUARANTEE ONE THIS SAME TICK
/// (correction, round 2 review -- an earlier version of this doc said "later in the same Tick"
/// unconditionally, which is only true on one of the two paths that reach here): MaybePoolPaint's
/// own fall-through runs right after ONLY when RunMaintenance was reached from Tick itself.
/// RunMaintenance is ALSO reached from PaintCountsIfChanged (Display.Heartbeat.cs), which never
/// calls MaybePoolPaint at all -- on the on-field path the false flag just sits, drain-checking
/// stays self-disabled (this method's own top-of-method guard needs _poolCovered true to run
/// again), until Engine's paint phase next takes the Tick(true) branch (BattleState.
/// ShouldPaintCard, Engine.cs) and MaybePoolPaint gets a turn to act on the flag.
/// </summary>
internal sealed partial class Display
{
    /// <summary>Per-region kills-site count latched at the moment overall coverage last
    /// (re-)latched via a REAL locate+scan (Display.PoolPaint.cs's MaybePoolPaint) -- deliberately
    /// NOT touched by that method's own cheap aggregate "shrank but still covers" fast-path
    /// re-latch: that branch must keep pointing at the last KNOWN-GOOD per-region shape, or a
    /// genuine drain would re-latch itself away before ReOfferDrainedRegions below ever got a
    /// chance to notice it. Keyed by the region's own (base,size) pair, which PoolLocator hands
    /// back byte-identical from its cache across calls (PoolLocator.CachedRegions, this commit's
    /// own new accessor) as long as the region has not relocated.</summary>
    private readonly Dictionary<(long baseAddr, long size), int> _regionCountAtCoverage = new();

    private void LatchRegionCounts(IReadOnlyList<(long baseAddr, long size)> regions)
    {
        _regionCountAtCoverage.Clear();
        var snapshot = _sites.Snapshot();
        foreach (var region in regions)
            _regionCountAtCoverage[region] = CountKillsSitesIn(snapshot, region.baseAddr, region.size);
    }

    // Internal, not private (LW-260 pin, zero behavior change): lets DisplayHeartbeatTests pin the
    // end-bound check (a site sitting exactly at rbase+rsize is OUTSIDE the region) directly,
    // mirroring PoolLocator.Restart.cs's ProgressLogEveryTicks test-accessor convention.
    internal static int CountKillsSitesIn(List<CardSites.Site> snapshot, long rbase, long rsize)
    {
        int count = 0;
        foreach (var s in snapshot)
            if (s.IsKills && s.SlotAddr >= rbase && s.SlotAddr < rbase + rsize) count++;
        return count;
    }

    /// <summary>Heals the specific gap MaybePoolPaint's own cheap aggregate re-latch cannot: losing
    /// ONE copy of a still-covered weapon (CoversAllMeta stays true because other copies survive)
    /// today just re-latches at the smaller aggregate count and never looks again. Re-offers ONLY
    /// the specific region(s) whose live kills-site count fell below its own <see
    /// cref="_regionCountAtCoverage"/> latch, via the EXISTING ScanPoolRegion (identical discovery/
    /// write/foreign-refusal code the initial pool scan already uses). Behind
    /// Tuning.CardReOfferEnabled as a kill switch (that constant's own doc has the reasoning). See
    /// this file's own class doc for why the post-re-offer coverage check is a re-decide, not a
    /// re-latch.</summary>
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

        // Re-DECIDE coverage rather than assume the re-offer found everything it lost (class doc:
        // the lesson this file was built around). A tracked id that has now lost its LAST copy
        // entirely (the whole region relocated or vanished, not just one redundant copy) must drop
        // _poolCovered so MaybePoolPaint's own fall-through can run the real relocate (LW-261:
        // PoolLocator.Step via Display.PoolLocate.cs's StepPoolLocate, never called directly from
        // here) -- but that only happens THIS Tick when RunMaintenance was reached from Tick
        // itself; from PaintCountsIfChanged (which never calls MaybePoolPaint) the flag just sits
        // until Tick next runs (class doc).
        if (!CoversAllMeta()) { _poolCovered = false; return; }

        LatchRegionCounts(new List<(long baseAddr, long size)>(_regionCountAtCoverage.Keys));
        _countAtCoverage = _sites.Count;
    }
}
