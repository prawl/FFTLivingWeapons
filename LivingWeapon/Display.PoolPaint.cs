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

    /// <summary>True if the sweep should be skipped this Tick. Locates the pool at most once
    /// per coverage window: a cached "already covering" flag short-circuits every subsequent
    /// call until Invalidate() resets it (no per-tick rescan once covered, matching the
    /// existing maintenance/change-driven PaintAll paths that keep cached sites fresh).</summary>
    private bool MaybePoolPaint()
    {
        if (!_poolPaint) return false;
        if (_poolCovered) return true;

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
    /// discovery, write-discipline, and foreign-refusal code the whole-heap sweep uses.</summary>
    private void ScanPoolRegion(long regionBase, long regionSize)
    {
        var reader = new ChunkReader(_mem);
        long rend = regionBase + regionSize;
        long chunkStart = regionBase;
        while (chunkStart < rend)
        {
            int read = reader.ReadInRegion(chunkStart, regionBase, rend, out int lookback, out int searchable);
            if (read == 0) break;
            OnChunk(reader.Buf, lookback, searchable, chunkStart - lookback);
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
