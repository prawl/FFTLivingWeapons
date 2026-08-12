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
    /// staying blind until Invalidate() is called from somewhere else.</summary>
    private bool MaybePoolPaint()
    {
        if (!_poolPaint) return false;
        if (_poolCovered)
        {
            if (_sites.Count == _countAtCoverage) return true;                 // cheap steady-state: one int compare
            if (CoversAllMeta()) { _countAtCoverage = _sites.Count; return true; }  // shrank but still covers: re-latch
            _poolCovered = false;                                              // drained below coverage: fall through to re-locate
        }

        var regions = _poolLocator.LocateAll();
        if (regions.Count == 0)
        {
#if LWDEV
            ModLogger.Debug(LogVerb.Display, "LW37 paint: no named-pool region located; sweep fallback");
#endif
            return false;
        }

        // Paint EVERY name-bearing baked region: the card materializes from one of them and there
        // is no static signature for which, so covering them all guarantees the read source is painted.
        foreach (var (rbase, rsize) in regions) ScanPoolRegion(rbase, rsize);

        _poolCovered = CoversAllMeta();
        if (_poolCovered) _countAtCoverage = _sites.Count;   // LW-163: snapshot the count the re-check above compares against
#if LWDEV
        int killsIds = 0;
        foreach (var s in _sites.Snapshot()) if (s.IsKills) killsIds++;
        ModLogger.Debug(LogVerb.Display, $"LW37 paint: {regions.Count} region(s), kills sites={killsIds}, meta ids={_meta.Count}, coverage={_poolCovered}");
#endif
        return _poolCovered;
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
    /// tick today) and is observable in the LWDEV coverage log line below.</summary>
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
