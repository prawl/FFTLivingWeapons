using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pool-anchored in-place equip-card Kills paint. Write, compose, attribution and
/// foreign-refusal come from the private OnChunk (Display.cs). This partial locates the writable
/// UE string pool, scans it once via OnChunk, and skips the heap DisplaySweep once every tracked id has a kills site.
/// </summary>
internal sealed partial class Display
{
    private readonly bool _poolPaint;
    // Internal so DisplayPoolLocateBudgetTests can read PublishGeneration off it,
    // same test-accessor habit as Display.Flight.cs.
    internal readonly PoolLocator _poolLocator;
    private bool _poolCovered;
    // Site count in the cache when coverage last latched true. While covered nothing else
    // offers sites to the cache, so it can only shrink (PaintAll evicts a site whose anchor stopped
    // verifying, e.g. after a save-load). Same count means same coverage; a lower count triggers the re-check below.
    private int _countAtCoverage;

    // True once pool coverage has gone false->true in this launch. Never reset, so it
    // is independent of _poolCovered. The first latch logs at Info (kill counters went live);
    // later re-latches (Invalidate, the drained-cache heal) stay Debug.
    private bool _coverageAnnounced;

    // The StuckEdge and coverage-transition log gates (NoPoolRegionLogGate, ReArmNoPoolRegionLog,
    // PoolCoverageLogGate) live in Display.PoolPaintLog.cs. The cadence state for the full-region scan
    // (ShouldRunFullPoolScan, PoolScanPassesForTest) lives in Display.PoolPaintCadence.cs.

    /// <summary>True if the sweep should be skipped this Tick. Once coverage latches, a count compare
    /// against _countAtCoverage short-circuits later calls. A shrink that still covers every
    /// tracked id re-latches at the new count; a shrink below that clears the latch and the scan runs
    /// again, which is how a save-load that frees the cached buffers recovers.
    ///
    /// CardSites keeps a dead site for Tuning.CardEvictStrikes-1 maintenance beats
    /// (CardSites.Verify.cs), so the latch holds until eviction actually drops Count. A same-beat
    /// "something looked wrong" signal was tried and reverted. It re-scanned Regions and every region's
    /// contents (143-569ms, live-measured) on any single site's transient miss, which is routine across
    /// ~1400-2000 live sites. The tests that used to pin single-beat eviction
    /// (DisplayMaintenanceTests.Dead_sites_evicted_by_maintenance_pass_after_the_strike_window and two in
    /// DisplayPoolPaintTests.cs) now drive Tuning.CardEvictStrikes beats first.
    ///
    /// Retune round. This method only reads the published PoolLocator.CachedRegions and neither
    /// triggers nor steps a region walk. The old synchronous PoolLocator.LocateAll here blocked for
    /// 7 to 10 seconds (live-measured). After the resumable rewrite, also stepping the locate here
    /// paid the ~45ms region snapshot about twice a tick, roughly 32 percent of one 92.2s owner-tape scan
    /// and 29 percent of a 101.7s one (arithmetic in PoolScan.cs SnapshotRefreshMs). Removing it does not
    /// shorten scan wall time, SnapshotRefreshMs and the battle-aware budget do that. Engine's "pool-locate"
    /// lane (Display.PoolLocate.cs StepPoolLocate) is the only driver, so a bare Display under test steps
    /// it itself (CardFixtures.TickWithPoolLocate).</summary>
    private bool MaybePoolPaint()
    {
        if (!_poolPaint) return false;
        if (_poolCovered)
        {
            if (_sites.Count == _countAtCoverage) return true;                 // cheap steady-state. One int compare
            if (CoversAllMeta()) { _countAtCoverage = _sites.Count; return true; }  // shrank but still covers. Re-latch
            _poolCovered = false;                                              // drained below coverage. Fall through to re-locate
        }

        var regions = _poolLocator.CachedRegions;
        if (regions.Count == 0)
        {
            // Promoted from #if LWDEV, rate-limited by NoPoolRegionLogGate (Display.PoolPaintLog.cs).
            if (NoPoolRegionLogGate())
                ModLogger.Debug(LogVerb.Display, "LW37 paint: no named-pool region located; sweep fallback");
            return false;
        }
        ReArmNoPoolRegionLog();   // the next drought gets its own transition line

        // Commit 1B. Scan every name-bearing baked region, but only when ShouldRunFullPoolScan allows.
        // On an off-cadence tick the sites cache is unchanged and Tick falls through to the sweep.
        if (ShouldRunFullPoolScan())
        {
            long t0 = _nowMs();
            foreach (var (rbase, rsize) in regions) ScanPoolRegion(rbase, rsize);
            PoolScanPassesForTest++;
            // Prefix differs from "LW37 paint:" because DisplayPoolPaintTests.Persistent_partial_coverage_logs_the_summary_line_once_not_every_tick
            // filters on that phrase.
            ModLogger.Debug(LogVerb.Display, $"LW37 region-scan: took {_nowMs() - t0}ms across {regions.Count} region(s)");
        }

        _poolCovered = CoversAllMeta();
        if (_poolCovered)
        {
            _countAtCoverage = _sites.Count;   // snapshot the count the re-check above compares against
            LatchRegionCounts(regions);        // This beat's per-region baseline (own doc)
            AnnounceCoverage(regions);         // This is always a false->true edge (see field doc)
        }
        // Promoted from #if LWDEV, rate-limited by PoolCoverageLogGate (Display.PoolPaintLog.cs).
        // Snapshot is copied only when the gate logs.
        if (PoolCoverageLogGate(_poolCovered))
        {
            int killsIds = 0;
            foreach (var s in _sites.Snapshot()) if (s.IsKills) killsIds++;
            ModLogger.Debug(LogVerb.Display, $"LW37 paint: {regions.Count} region(s), kills sites={killsIds}, meta ids={_meta.Count}, coverage={_poolCovered}");
        }
        return _poolCovered;
    }

    /// <summary>Called from the false->true latch site above only; the re-latch in the
    /// short-circuit at the top of <see cref="MaybePoolPaint"/> never reaches here. The first call of a
    /// launch is the "kill counters just went live" moment, logged at Info with the paint-spot count and
    /// the time since Display's first Tick (Engine ticks Display only after the launch guard arms).
    /// Later calls (post-Invalidate re-latch, drained-cache heal) log at Debug.</summary>
    private void AnnounceCoverage(IReadOnlyList<(long baseAddr, long size)> regions)
    {
        // One snapshot shared by killsSites and the flight tap (RecordCoverageIfTapped),
        // which each used to copy their own.
        var snapshot = _sites.Snapshot();
        int killsSites = 0;
        foreach (var s in snapshot) if (s.IsKills) killsSites++;

        // Captured before _coverageAnnounced flips; used by both branches and the flight tap.
        string trigger = _coverageAnnounced ? "re-latch" : "first";

        if (!_coverageAnnounced)
        {
            _coverageAnnounced = true;
            double seconds = _firstTickMs < 0 ? 0.0 : (_nowMs() - _firstTickMs) / 1000.0;
            // _sites.Count (kills and suffix sites) is appended so headroom against CardSites.MaxSites (2048) is readable from this line.
            ModLogger.Event(LogVerb.Display,
                $"The kill counters are live on the equip cards: {killsSites} paint spots across {regions.Count} pool region(s), {seconds:0.0}s after the mod armed. ({_sites.Count}/{CardSites.MaxSites} cache slots in use)");
        }
        else
        {
            ModLogger.Debug(LogVerb.Display,
                $"pool coverage re-established: {killsSites} spots across {regions.Count} region(s) ({_sites.Count}/{CardSites.MaxSites} cache slots in use)");
        }

        RecordCoverageIfTapped(regions, killsSites, trigger, snapshot);   // Flight.Record tap, Display.Flight.cs
    }

    /// <summary>Walk the located pool region in chunks (ChunkReader Lookback/TrailSlack bounds, as in
    /// DisplaySweep) and feed each chunk to OnChunk, so the pool path shares the sweep's discovery,
    /// write discipline and foreign-refusal code.
    ///
    /// allSuffixes. True searches every tracked id's suffix in a text-bearing chunk, not just the
    /// rotation slice, so a tally reset cannot leave a stale +N. Cost is about 121 ids x 2 encodings of
    /// FindAll passes per such chunk, tens of ms for a full 4MB chunk, on Engine's background loop and not
    /// the game thread. It runs on battle-exit, paused status-card and battle-enter Invalidate (see
    /// Engine.cs) and on the drained-cache re-latch, which can land mid-battle during an off-field settle.
    /// If kills coverage never latches (a weapon absent from every pool region) that cost recurs per tick.
    /// That failure already existed and shows in the "LW37 paint" coverage line (Debug tier).</summary>
    private void ScanPoolRegion(long regionBase, long regionSize)
    {
        var reader = new ChunkReader(_mem);
        long rend = regionBase + regionSize;
        long chunkStart = regionBase;
        while (chunkStart < rend)
        {
            int read = reader.ReadInRegion(chunkStart, regionBase, rend, out int lookback, out int searchable);
            if (read == 0) break;
            OnChunk(reader.Buf, lookback, searchable, chunkStart - lookback, allSuffixes: true);
            chunkStart += ChunkReader.ChunkSize;
        }
    }

    /// <summary>True when every tracked weapon id has a live kills Site registered.</summary>
    private bool CoversAllMeta()
    {
        var covered = new HashSet<int>();
        foreach (var s in _sites.Snapshot())
            if (s.IsKills) covered.Add(s.Id);

        foreach (int id in _meta.Keys)
            if (!covered.Contains(id)) return false;
        return true;
    }
}
