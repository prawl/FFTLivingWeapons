using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

// Partial: hot-phase execution and chunk read helpers.  See DisplaySweep.cs for architecture.
internal sealed partial class DisplaySweep
{
    /// <summary>
    /// Execute the hot phase: re-offer chunks that previously yielded paint sites.
    /// Budget is shared with the background phase; after the first chunk the loop stops
    /// when remaining bytes reach zero. A rotation cursor ensures no chunk is permanently
    /// starved when the budget is tight. TTL-expired chunks are evicted before the pass.
    /// Returns the remaining budget after the hot phase completes.
    /// </summary>
    private long RunHotPhase(long now, long remaining, ChunkHandler onChunk)
    {
        bool hotDue = _rescanRequested || (now - _lastHotPassMs >= HotRescanMs);
        if (!hotDue || _hotChunks.Count == 0)
        {
            _rescanRequested = false;
            return remaining;
        }

        _rescanRequested = false;
        _lastHotPassMs   = now;

        // Evict TTL-expired chunks before iterating.
        var toRemove = new List<long>();
        foreach (var kv in _hotChunks)
            if (now - kv.Value > HotTtlMs) toRemove.Add(kv.Key);
        foreach (long addr in toRemove) _hotChunks.Remove(addr);

        if (_hotChunks.Count == 0) return remaining;

        var hotList = new List<long>(_hotChunks.Keys);
        hotList.Sort();
        int count = hotList.Count;

        // Wrap rotation cursor.
        if (_hotCursor >= count) _hotCursor = 0;
        int start        = _hotCursor;
        bool didOne      = false;
        var removeAfter  = new List<long>();

        for (int i = 0; i < count; i++)
        {
            // After the first chunk honour the budget so hot-phase doesn't
            // consume all of remaining (worst case: 64 chunks × ~4 MB = 268 MB).
            if (didOne && remaining <= 0) break;

            int idx        = (start + i) % count;
            long chunkStart = hotList[idx];
            int read = ReadChunk(chunkStart, out int lookback, out int searchable);
            if (read == 0) { removeAfter.Add(chunkStart); continue; }

            onChunk(_buf, lookback, searchable, chunkStart - lookback);
            remaining -= read;
            didOne     = true;
            // Advance the rotation cursor so the next pass picks up where this one left off.
            _hotCursor = (idx + 1) % count;
        }

        foreach (long addr in removeAfter) _hotChunks.Remove(addr);
        return remaining;
    }

    /// <summary>Read a hot chunk with correct lookback, using the per-Tick region snapshot.
    /// Returns bytes read (0 on failure).</summary>
    private int ReadChunk(long chunkStart, out int lookback, out int searchable)
    {
        lookback   = 0;
        searchable = 0;
        foreach (var (rbase, rsize) in _regionSnap)
        {
            if (chunkStart < rbase || chunkStart >= rbase + rsize) continue;
            return ReadChunkInRegion(chunkStart, rbase, rbase + rsize, out lookback, out searchable);
        }
        return 0;
    }

    /// <summary>Read [chunkStart - L, chunkStart + ChunkSize + TrailSlack) clamped to the region.
    /// Clears the buffer tail past readLen to prevent stale bytes from a prior longer read from
    /// leaking into the scanner (region-end chunks are shorter than a full chunk).
    /// Returns bytes read into _buf (0 on failure).</summary>
    private int ReadChunkInRegion(long chunkStart, long rbase, long rend,
                                  out int lookback, out int searchable)
    {
        lookback   = (int)Math.Min(Lookback, chunkStart - rbase);
        long readStart = chunkStart - lookback;
        long readEnd   = Math.Min(rend, chunkStart + ChunkSize + TrailSlack);
        int readLen    = (int)(readEnd - readStart);

        if (readLen <= 0 || readLen > _buf.Length) { lookback = 0; searchable = 0; return 0; }
        int got = _mem.ReadInto(readStart, _buf, readLen);
        if (got == 0) { lookback = 0; searchable = 0; return 0; }

        // Clear tail so stale bytes from a previous longer read don't leak into the scanner.
        if (readLen < _buf.Length)
            Array.Clear(_buf, readLen, Math.Min(512, _buf.Length - readLen));

        searchable = (int)Math.Min(ChunkSize, rend - chunkStart);
        return got;
    }
}
