using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// Chunk-aligned addresses that recently yielded paint sites, re-offered ahead of the background walk.
/// Handles LRU cap eviction, TTL expiry, pass cadence, and a rotation cursor so a tight budget starves no chunk.
/// </summary>
internal sealed class HotChunkSet
{
    public const long HotRescanMs = 250;
    public const int MaxHotChunks = 64;
    public const long HotTtlMs = 10_000;

    // chunk-aligned address -> last Mark time (ms); LRU eviction past MaxHotChunks
    private readonly Dictionary<long, long> _chunks = new();

    // index into the sorted chunk list where the next pass resumes
    private int _cursor = 0;

    // starts far enough back to be overdue at t=0
    private long _lastPassMs = long.MinValue / 2;
    private bool _rescanRequested = false;

    public int Count => _chunks.Count;

    /// <summary>Record that a chunk yielded paint sites and refresh its TTL.</summary>
    public void Mark(long chunkStart, long now)
    {
        bool wasEmpty = _chunks.Count == 0;
        _chunks[chunkStart] = now;

        // Over the cap, drop the oldest-marked chunk.
        while (_chunks.Count > MaxHotChunks)
        {
            long oldest = _chunks.MinBy(kv => kv.Value).Key;
            _chunks.Remove(oldest);
        }

        // Start the rescan countdown from this mark.
        if (wasEmpty) _lastPassMs = now;
    }

    /// <summary>A count or target change: rescan hot chunks on the next pass regardless of clock.</summary>
    public void RequestRescan() => _rescanRequested = true;

    /// <summary>True when a pass is due (rescan request or HotRescanMs elapsed) and the set is non-empty; stamps the
    /// pass time. The request is consumed even when the set is empty.</summary>
    public bool TryBeginPass(long now)
    {
        bool due = _rescanRequested || (now - _lastPassMs >= HotRescanMs);
        if (!due || _chunks.Count == 0)
        {
            _rescanRequested = false;
            return false;
        }
        _rescanRequested = false;
        _lastPassMs = now;
        return true;
    }

    /// <summary>Drop chunks whose last Mark is older than HotTtlMs.</summary>
    public void EvictExpired(long now)
    {
        var toRemove = new List<long>();
        foreach (var kv in _chunks)
            if (now - kv.Value > HotTtlMs) toRemove.Add(kv.Key);
        foreach (long addr in toRemove) _chunks.Remove(addr);
    }

    /// <summary>Chunks in ascending address order, cursor wrapped into range. Callers iterate (Cursor + i) % Count
    /// and report each processed chunk via NoteProcessed.</summary>
    public List<long> SortedSnapshot()
    {
        var list = new List<long>(_chunks.Keys);
        list.Sort();
        if (_cursor >= list.Count) _cursor = 0;
        return list;
    }

    /// <summary>Rotation start index for this pass (wrapped into range by SortedSnapshot).</summary>
    public int Cursor => _cursor;

    /// <summary>Advance the cursor past idx. Dead or budget-skipped chunks do not advance it.</summary>
    public void NoteProcessed(int idx, int count) => _cursor = (idx + 1) % count;

    /// <summary>Drop a chunk whose read failed (its region is gone).</summary>
    public void Remove(long chunkStart) => _chunks.Remove(chunkStart);
}
