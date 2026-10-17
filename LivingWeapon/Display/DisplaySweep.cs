using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Byte-budgeted, resumable heap sweep for the display paint pipeline. Replaces the synchronous
/// full scan that froze the 100ms engine loop for ~9s and the hot-region design whose empty result
/// re-armed a back-to-back full scan.
/// Each Tick runs a hot phase (re-offer chunks that held card text, rotating the start so none starve)
/// then a background phase (cursor-based generational walk) within one shared byte budget.
/// A finished generation waits GenerationRestMs after completion before restarting.
/// Hot-chunk lifetime lives in <see cref="HotChunkSet"/>; chunk reads and region snapshots in <see cref="ChunkReader"/>.
/// </summary>
internal sealed class DisplaySweep
{
    public delegate void ChunkHandler(byte[] buf, int lookback, int searchable, long bufBaseAddr);

    public const int ChunkSize = ChunkReader.ChunkSize;
    public const int Lookback = ChunkReader.Lookback;
    public const int TrailSlack = ChunkReader.TrailSlack;
    public const long HotRescanMs = HotChunkSet.HotRescanMs;
    public const long GenerationRestMs = 90_000;
    public const long GenerationMinGapMs = 5_000;
    public const int MaxHotChunks = HotChunkSet.MaxHotChunks;
    public const long HotTtlMs = HotChunkSet.HotTtlMs;

    private readonly Func<long> _nowMs;
    private readonly ChunkReader _reader;
    private readonly HotChunkSet _hot = new();

    // generation state
    private long _generation;
    private long _genStartTime = long.MinValue / 2;
    private long _genCompleteTime = long.MinValue / 2;
    private bool _genComplete = false;
    private bool _invalidatePending = false;

    // background walk cursor
    private long _cursor = 0;

    public DisplaySweep(IGameMemory mem, Func<long> nowMs)
    {
        _reader = new ChunkReader(mem);
        _nowMs = nowMs;
        StartGeneration(nowMs());
    }

    /// <summary>Increments when a new full pass starts.</summary>
    public long Generation => _generation;

    /// <summary>True once the current generation has exhausted all regions.</summary>
    public bool IsComplete => _genComplete;

    /// <summary>Record that a chunk yielded paint sites. Keep it hot, refresh its TTL, and reset the hot-rescan countdown.</summary>
    public void MarkHot(long chunkStart) => _hot.Mark(chunkStart, _nowMs());

    /// <summary>A count or target change. Rescan hot chunks on the next Tick regardless of clock.</summary>
    public void RequestRescan() => _hot.RequestRescan();

    /// <summary>Menu buffers reallocated. Start a new generation soon (subject to GenerationMinGapMs). Hot chunks are kept.</summary>
    public void Invalidate() => _invalidatePending = true;

    /// <summary>Drive one cycle, hot phase then background phase, sharing budgetBytes. At least one background chunk runs while the generation is active.</summary>
    public void Tick(long budgetBytes, ChunkHandler onChunk)
    {
        long now = _nowMs();
        long remaining = budgetBytes;

        // Snapshot regions once and reuse them for both phases.
        _reader.Snapshot();

        remaining = RunHotPhase(now, remaining, onChunk);

        if (_genComplete)
        {
            // Rest counts from generation completion, so a slow pass does not restart instantly.
            bool restElapsed = (now - _genCompleteTime) >= GenerationRestMs;
            bool invalidateReady = _invalidatePending && (now - _genStartTime) >= GenerationMinGapMs;
            if (!restElapsed && !invalidateReady) return;
        }

        if (_genComplete)
        {
            _invalidatePending = false;
            StartGeneration(now);
            remaining = budgetBytes; // full budget for new generation's first tick
        }

        bool firstChunk = true;
        foreach (var (rbase, rsize) in _reader.Regions)
        {
            long rend = rbase + rsize;
            if (rend <= _cursor) continue; // region entirely before cursor
            long chunkStart = Math.Max(rbase, _cursor);

            while (chunkStart < rend)
            {
                if (!firstChunk && remaining <= 0) { _cursor = chunkStart; return; }
                firstChunk = false;

                int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
                if (read == 0) { chunkStart += ChunkSize; continue; }

                onChunk(_reader.Buf, lookback, searchable, chunkStart - lookback);
                remaining -= read;
                chunkStart += ChunkSize;
            }
        }

        _cursor = 0;
        _genComplete = true;
        _genCompleteTime = now;
    }

    /// <summary>Hot phase. Re-offer chunks that yielded paint sites, evicting TTL-expired ones first.
    /// A rotation cursor keeps a tight budget from starving any chunk. Returns the remaining budget.</summary>
    private long RunHotPhase(long now, long remaining, ChunkHandler onChunk)
    {
        if (!_hot.TryBeginPass(now)) return remaining;

        _hot.EvictExpired(now);
        if (_hot.Count == 0) return remaining;

        var hotList = _hot.SortedSnapshot();
        int count = hotList.Count;
        int start = _hot.Cursor;
        bool didOne = false;
        var removeAfter = new List<long>();

        for (int i = 0; i < count; i++)
        {
            // Past the first chunk, honor the budget (worst case 64 chunks x ~4 MB = 268 MB).
            if (didOne && remaining <= 0) break;

            int idx = (start + i) % count;
            long chunkStart = hotList[idx];
            int read = _reader.Read(chunkStart, out int lookback, out int searchable);
            if (read == 0) { removeAfter.Add(chunkStart); continue; }

            onChunk(_reader.Buf, lookback, searchable, chunkStart - lookback);
            remaining -= read;
            didOne = true;
            _hot.NoteProcessed(idx, count);
        }

        foreach (long addr in removeAfter) _hot.Remove(addr);
        return remaining;
    }

    private void StartGeneration(long now)
    {
        _generation++;
        _genStartTime = now;
        _genComplete = false;
        _cursor = 0;
    }
}
