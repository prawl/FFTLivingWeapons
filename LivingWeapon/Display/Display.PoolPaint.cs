using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-37: pool-anchored in-place equip-card Kills paint. The write, compose, attribution, and
/// foreign-refusal all come from the EXISTING private OnChunk (Display.cs); this partial only
/// adds the orchestration: locate the writable UE string pool, scan it ONCE via OnChunk, and
/// skip the whole-heap DisplaySweep once every tracked id has a live kills site.
/// </summary>
internal sealed partial class Display
{
    private readonly bool _poolPaint;
    private readonly PoolLocator _poolLocator;
    private bool _poolCovered;
    // LW-163: the site count the cache held the moment coverage last latched true. While
    // _poolCovered is true, nothing else in this Tick offers a site to the pool cache (the
    // sweep and the pool scan below are both skipped), so the cache can only ever SHRINK, via
    // a maintenance/count-change PaintAll evicting a site whose anchor no longer verifies (a
    // save-load reallocated the buffer it pointed at). An unchanged count therefore means
    // unchanged coverage; a changed count is the drained-cache signal the re-check below acts on.
    private int _countAtCoverage;

    // LW-165 stage 1: latches true the first time pool coverage goes false->true this process
    // launch, and never resets (deliberately independent of _poolCovered's own per-window
    // resets). Gates AnnounceCoverage's Info vs. Debug split below: the FIRST latch is the
    // "kill counters just went live" moment the owner needs a stopwatch on; every LATER
    // false->true edge (a post-Invalidate re-latch, e.g. battle exit/enter or the LW-163
    // drained-cache heal) is a re-coverage, not first light, and stays Debug/file-only.
    private bool _coverageAnnounced;

    // LW-257: the StuckEdge/coverage-transition log gates (NoPoolRegionLogGate/
    // ReArmNoPoolRegionLog/PoolCoverageLogGate, called below) live in Display.PoolPaintLog.cs --
    // a real seam once this file crossed the 200-line trigger; see that file's class doc.

    /// <summary>True if the sweep should be skipped this Tick. Locates the pool at most once
    /// per coverage window: a cached "already covering" flag short-circuits every subsequent
    /// call SO LONG AS the live site cache still covers every tracked id. The short-circuit is
    /// no longer a one-way latch (LW-163): a cheap count compare against the cache's size at the
    /// moment coverage latched detects a shrink (the only way it can move while covered, see
    /// _countAtCoverage's own comment); a shrink that still covers everything just re-latches at
    /// the new count, and a shrink that drains coverage below every tracked id clears the latch
    /// so the locate/scan below runs again on this same Tick. A save-load that frees the buffers
    /// the cached sites point into is exactly that drain: CardSites.PaintAll's anchor re-verify
    /// evicts the dead sites first (maintenance or count-change PaintAll, both ahead of this call
    /// in Display.Tick), this method sees the smaller count next, and re-locates instead of
    /// staying blind until Invalidate() is called from somewhere else.
    ///
    /// LW-257: CardSites' anchor-verify leniency (CardSites.Verify.cs) means a genuinely dead
    /// site now survives Tuning.CardEvictStrikes-1 maintenance beats before PaintAll actually
    /// evicts it, so the count compare below stays latched (by design) for those beats too --
    /// this method only re-locates once eviction has actually happened and Count has actually
    /// dropped, exactly as it did before this arc. A same-beat "something looked wrong" signal
    /// was tried in-tree and deliberately reverted (no doc row states this -- the rationale lives
    /// only here, next to the code it explains): it re-scanned Regions() plus every
    /// region's full contents (143-569ms, live-measured) on ANY single site's
    /// transient miss, which happens routinely across ~1400-2000 live sites, trading a rare
    /// whole-id-lost heal for a frequent single-site one -- backwards from this arc's own cost
    /// budget. The three tests that used to pin single-beat eviction (DisplayMaintenanceTests.
    /// Dead_sites_evicted_by_maintenance_pass_after_the_strike_window and two in
    /// DisplayPoolPaintTests.cs) now drive Tuning.CardEvictStrikes beats before asserting the
    /// heal, matching this method's real behavior instead of the pre-fix one.
    ///
    /// LW-261: this method no longer triggers or blocks on a region walk at all. It calls <see
    /// cref="StepPoolLocate"/> (Display.PoolLocate.cs) -- the SAME method Engine's own
    /// "pool-locate" tick lane calls independently every tick -- and then only ever READS the
    /// currently-published result (PoolLocator.CachedRegions). The old synchronous
    /// PoolLocator.LocateAll call that used to sit here blocked this same call path for 7 to 10
    /// seconds, live-measured; that walk is now resumable and budgeted (PoolLocator.cs's own
    /// class doc). Calling StepPoolLocate from here too (not only from Engine's own lane) is
    /// deliberate: it means a bare Display driven only by repeated Tick() calls -- every existing
    /// unit test, and any future caller that never wires an Engine -- still drives its own scan to
    /// completion, exactly as LocateAll always did, with the two call sites racing to complete the
    /// SAME scan on a real engine tick being a harmless minor over-spend against the per-tick
    /// budget, never a correctness issue.</summary>
    private bool MaybePoolPaint()
    {
        if (!_poolPaint) return false;
        if (_poolCovered)
        {
            if (_sites.Count == _countAtCoverage) return true;                 // cheap steady-state: one int compare
            if (CoversAllMeta()) { _countAtCoverage = _sites.Count; return true; }  // shrank but still covers: re-latch
            _poolCovered = false;                                              // drained below coverage: fall through to re-locate
        }

        StepPoolLocate();
        var regions = _poolLocator.CachedRegions;
        if (regions.Count == 0)
        {
            // LW-257: promoted from #if LWDEV, rate-limited via NoPoolRegionLogGate (item 7,
            // Display.PoolPaintLog.cs): a persistent no-pool state logs its transition once.
            if (NoPoolRegionLogGate())
                ModLogger.Debug(LogVerb.Display, "LW37 paint: no named-pool region located; sweep fallback");
            return false;
        }
        ReArmNoPoolRegionLog();   // the next drought gets its own transition line

        // Paint EVERY name-bearing baked region: the card materializes from one of them and there
        // is no static signature for which, so covering them all guarantees the read source is painted.
        foreach (var (rbase, rsize) in regions) ScanPoolRegion(rbase, rsize);

        _poolCovered = CoversAllMeta();
        if (_poolCovered)
        {
            _countAtCoverage = _sites.Count;   // LW-163: snapshot the count the re-check above compares against
            LatchRegionCounts(regions);        // LW-257 commit 2: this beat's per-region baseline (own doc)
            AnnounceCoverage(regions);         // LW-165 stage 1: this is always a false->true edge (see field doc)
        }
        // LW-257: promoted from #if LWDEV, rate-limited via PoolCoverageLogGate (Display.
        // PoolPaintLog.cs -- see its own doc for what drives repeated fall-through). The
        // Snapshot() copy is paid ONLY when the gate actually logs.
        if (PoolCoverageLogGate(_poolCovered))
        {
            int killsIds = 0;
            foreach (var s in _sites.Snapshot()) if (s.IsKills) killsIds++;
            ModLogger.Debug(LogVerb.Display, $"LW37 paint: {regions.Count} region(s), kills sites={killsIds}, meta ids={_meta.Count}, coverage={_poolCovered}");
        }
        return _poolCovered;
    }

    /// <summary>LW-165 stage 1: called only from the false->true coverage-latch site above (the
    /// re-check inside the short-circuit at the top of <see cref="MaybePoolPaint"/> re-latches an
    /// ALREADY-true _poolCovered and never reaches here -- it is a coverage continuation, not a
    /// transition). The first call of the process launch is the "kill counters just went live"
    /// moment: logs at Info, with the paint-spot count and how long it took since Display's own
    /// first Tick (Engine only starts ticking Display after the launch guard arms, so that stamp
    /// times the gap the owner actually feels). Every later call (a post-Invalidate re-latch, e.g.
    /// battle exit/enter or the LW-163 drained-cache heal) logs at Debug instead: still evidence
    /// in the file, never console noise.</summary>
    private void AnnounceCoverage(IReadOnlyList<(long baseAddr, long size)> regions)
    {
        int killsSites = 0;
        foreach (var s in _sites.Snapshot()) if (s.IsKills) killsSites++;

        // LW-257: captured before _coverageAnnounced flips, for both branches below and the
        // flight tap's own "trigger" field.
        string trigger = _coverageAnnounced ? "re-latch" : "first";

        if (!_coverageAnnounced)
        {
            _coverageAnnounced = true;
            double seconds = _firstTickMs < 0 ? 0.0 : (_nowMs() - _firstTickMs) / 1000.0;
            // LW-257: _sites.Count (every cached site, kills AND suffix) appended so headroom
            // against CardSites.MaxSites (2048) is readable straight off this line instead of
            // needing a second, separate measurement pass. The cap itself is untouched.
            ModLogger.Event(LogVerb.Display,
                $"The kill counters are live on the equip cards: {killsSites} paint spots across {regions.Count} pool region(s), {seconds:0.0}s after the mod armed. ({_sites.Count}/{CardSites.MaxSites} cache slots in use)");
        }
        else
        {
            ModLogger.Debug(LogVerb.Display,
                $"pool coverage re-established: {killsSites} spots across {regions.Count} region(s) ({_sites.Count}/{CardSites.MaxSites} cache slots in use)");
        }

        RecordCoverageIfTapped(regions, killsSites, trigger);   // LW-257: Flight.Record tap, Display.Flight.cs
    }

    /// <summary>Walk the located pool region in chunks (ChunkReader's own Lookback/TrailSlack
    /// bounds, exactly like DisplaySweep's background walk) and feed each chunk to the
    /// EXISTING private OnChunk: the pool path registers sites through the identical
    /// discovery, write-discipline, and foreign-refusal code the whole-heap sweep uses.
    ///
    /// allSuffixes: true (LW-59): a text-bearing chunk (one with a kills hit) searches every
    /// tracked id's suffix, not just the rotation slice, so a tally reset can never leave a
    /// stale +N behind for lack of that id's turn in the rotation. Honest cost accounting: this
    /// is roughly 121 ids x 2 encodings of vectorized FindAll passes over each text-bearing
    /// pool chunk. The observed live pools are small, but a real pool can exceed ChunkSize
    /// (PoolLocator's own caveat), so the worst case is that many passes over a full 4MB chunk,
    /// roughly tens of ms per such chunk, landing on the ENGINE'S OWN background loop (never
    /// the game thread) at the ticks that already run this scan: battle-exit Invalidate, a
    /// paused status-card Invalidate, the battle-enter Invalidate (LW-163, Engine.cs), or the
    /// drained-cache re-latch above (LW-163: the count-gated re-check clearing _poolCovered),
    /// which can also land mid-battle during an off-field settle stretch rather than only at an
    /// edge, where hold-reassert/kill-poll latency does not bite.
    /// Residual accepted risk: if kills coverage never latches (a future weapon absent from
    /// every pool region), that text-chunk cost recurs per tick. That never-latch state is a
    /// pre-existing failure mode (it already re-scans all regions AND runs the sweep every
    /// tick today) and is observable in the "LW37 paint" coverage line (MaybePoolPaint below) --
    /// unconditional Debug since LW-257; LWDEV-only before that promotion.</summary>
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
