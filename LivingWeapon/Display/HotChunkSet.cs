using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The hot-chunk set for the display sweep: chunk-aligned addresses that recently yielded
/// paint sites, re-offered on a short cadence ahead of the background walk. Owns the whole
/// lifetime policy in one place: LRU cap eviction on Mark, TTL expiry (chunks not re-marked
/// within HotTtlMs drain out automatically), the pass cadence (HotRescanMs clock or an
/// explicit rescan request), and the rotation cursor that keeps a tight byte budget from
/// permanently starving any chunk.
/// </summary>
internal sealed class HotChunkSet
{
    public const long HotRescanMs  = 250;
    public const int  MaxHotChunks = 64;
    public const long HotTtlMs     = 10_000;

    // chunk-aligned address -> last Mark timestamp (ms)
    // Eviction is LRU (oldest lastMarked) when count exceeds MaxHotChunks.
    private readonly Dictionary<long, long> _chunks = new();

    // rotation cursor: index into the sorted chunk list at which the hot pass resumes
    // so no chunk is permanently starved when the budget allows fewer than MaxHotChunks.
    private int _cursor = 0;

    // hot phase timing -- sentinel far enough back to be "overdue" at t=0
    private long _lastPassMs = long.MinValue / 2;
    private bool _rescanRequested = false;

    public int Count => _chunks.Count;

    /// <summary>Record that a chunk at the given address yielded paint sites; keep it in
    /// the set and refresh its TTL so live chunks never expire.
    /// Resets the hot-rescan countdown so the consumer gets HotRescanMs before the next pass.</summary>
    public void Mark(long chunkStart, long now)
    {
        bool wasEmpty = _chunks.Count == 0;
        _chunks[chunkStart] = now;

        // Evict the chunk with the oldest lastMarked timestamp when over the cap.
        while (_chunks.Count > MaxHotChunks)
        {
            long oldest = _chunks.MinBy(kv => kv.Value).Key;
            _chunks.Remove(oldest);
        }

        // Start the countdown from this mark so caller gets a full HotRescanMs before re-offer.
        if (wasEmpty) _lastPassMs = now;
    }

    /// <summary>A count or target change: rescan hot chunks on the next pass regardless of clock.</summary>
    public void RequestRescan() => _rescanRequested = true;

    /// <summary>True when a hot pass is due (explicit request or HotRescanMs elapsed) AND the
    /// set is non-empty; stamps the pass time and consumes the rescan request. The request is
    /// consumed even when the set is empty, so a queued rescan never fires spuriously against
    /// chunks marked later.</summary>
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

    /// <summary>Evict chunks whose last Mark is older than HotTtlMs, so a region that stops
    /// yielding hits drains out of the set automatically.</summary>
    public void EvictExpired(long now)
    {
        var toRemove = new List<long>();
        foreach (var kv in _chunks)
            if (now - kv.Value > HotTtlMs) toRemove.Add(kv.Key);
        foreach (long addr in toRemove) _chunks.Remove(addr);
    }

    /// <summary>The chunks in ascending address order, with the rotation cursor wrapped into
    /// range. The caller iterates (Cursor + i) % Count and reports each chunk it actually
    /// processed via NoteProcessed.</summary>
    public List<long> SortedSnapshot()
    {
        var list = new List<long>(_chunks.Keys);
        list.Sort();
        if (_cursor >= list.Count) _cursor = 0;
        return list;
    }

    /// <summary>Rotation start index for this pass (wrapped into range by SortedSnapshot).</summary>
    public int Cursor => _cursor;

    /// <summary>Advance the rotation cursor so the next pass picks up where this one left off.
    /// Dead or budget-skipped chunks do not advance it.</summary>
    public void NoteProcessed(int idx, int count) => _cursor = (idx + 1) % count;

    /// <summary>Drop a chunk whose read failed (its region is gone).</summary>
    public void Remove(long chunkStart) => _chunks.Remove(chunkStart);
}
