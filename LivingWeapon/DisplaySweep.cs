using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Byte-budgeted, resumable heap sweep for the display paint pipeline. Replaces the
/// synchronous full scan that froze the 100ms engine loop for ~9s (missing kills during it)
/// and the hot-region design whose empty result re-armed a back-to-back full scan.
///
/// Each Tick: (1) HOT PHASE -- re-offer chunks that previously held card text, on a short
/// rescan interval or when explicitly requested, spending at most the shared byte budget
/// before stopping (rotating start position so no chunk is starved); (2) BACKGROUND PHASE
/// -- advance the generational walk by the remaining byte budget, resuming across Ticks
/// (cursor-based, self-healing when the address space shifts). A complete generation does
/// NOT restart until GenerationRestMs has elapsed after COMPLETION (not after start).
///
/// Hot-chunk lifetime (cap, TTL, cadence, rotation) lives in <see cref="HotChunkSet"/>;
/// chunk reads and the per-Tick region snapshot live in <see cref="ChunkReader"/>.
/// </summary>
internal sealed class DisplaySweep
{
    public delegate void ChunkHandler(byte[] buf, int lookback, int searchable, long bufBaseAddr);

    public const int ChunkSize           = ChunkReader.ChunkSize;
    public const int Lookback            = ChunkReader.Lookback;
    public const int TrailSlack          = ChunkReader.TrailSlack;
    public const long HotRescanMs        = HotChunkSet.HotRescanMs;
    public const long GenerationRestMs   = 90_000;
    public const long GenerationMinGapMs = 5_000;
    public const int MaxHotChunks        = HotChunkSet.MaxHotChunks;
    public const long HotTtlMs           = HotChunkSet.HotTtlMs;

    private readonly Func<long> _nowMs;
    private readonly ChunkReader _reader;
    private readonly HotChunkSet _hot = new();

    // generation state
    private long _generation;
    private long _genStartTime   = long.MinValue / 2;
    private long _genCompleteTime = long.MinValue / 2;
    private bool _genComplete    = false;
    private bool _invalidatePending = false;

    // background walk cursor
    private long _cursor = 0;

    public DisplaySweep(IGameMemory mem, Func<long> nowMs)
    {
        _reader = new ChunkReader(mem);
        _nowMs  = nowMs;
        StartGeneration(nowMs());
    }

    /// <summary>Increments when a new full pass starts.</summary>
    public long Generation => _generation;

    /// <summary>True once the current generation has exhausted all regions.</summary>
    public bool IsComplete => _genComplete;

    /// <summary>Record that a chunk at the given address yielded paint sites; keep it in
    /// the hot set and refresh its TTL so live chunks never expire.
    /// Resets the hot-rescan countdown so the consumer gets HotRescanMs before the next pass.</summary>
    public void MarkHot(long chunkStart) => _hot.Mark(chunkStart, _nowMs());

    /// <summary>A count or target change: rescan hot chunks on the next Tick regardless of clock.</summary>
    public void RequestRescan() => _hot.RequestRescan();

    /// <summary>Menu buffers reallocated: start a new generation ASAP (respecting GenerationMinGapMs).
    /// Hot chunks are retained (the new arena likely contains the same text).</summary>
    public void Invalidate() => _invalidatePending = true;

    /// <summary>
    /// Drive one cycle: HOT PHASE then BACKGROUND PHASE, sharing budgetBytes.
    /// At least one background chunk is always processed when the generation is active.
    /// Regions are snapshotted once per Tick to avoid re-walking the address space per hot chunk.
    /// </summary>
    public void Tick(long budgetBytes, ChunkHandler onChunk)
    {
        long now = _nowMs();
        long remaining = budgetBytes;

        // Snapshot regions once -- reused for both phases to avoid repeated VirtualQueryEx walks.
        _reader.Snapshot();

        remaining = RunHotPhase(now, remaining, onChunk);

        // BACKGROUND PHASE
        if (_genComplete)
        {
            // Rest is measured from generation COMPLETION (not start) so a slow pass
            // that nearly consumed GenerationRestMs doesn't restart instantly.
            bool restElapsed     = (now - _genCompleteTime) >= GenerationRestMs;
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

        // Exhausted all regions: generation complete.
        _cursor          = 0;
        _genComplete     = true;
        _genCompleteTime = now;
    }

    /// <summary>
    /// Execute the hot phase: re-offer chunks that previously yielded paint sites.
    /// Budget is shared with the background phase; after the first chunk the loop stops
    /// when remaining bytes reach zero. A rotation cursor ensures no chunk is permanently
    /// starved when the budget is tight. TTL-expired chunks are evicted before the pass.
    /// Returns the remaining budget after the hot phase completes.
    /// </summary>
    private long RunHotPhase(long now, long remaining, ChunkHandler onChunk)
    {
        if (!_hot.TryBeginPass(now)) return remaining;

        // Evict TTL-expired chunks before iterating.
        _hot.EvictExpired(now);
        if (_hot.Count == 0) return remaining;

        var hotList = _hot.SortedSnapshot();
        int count   = hotList.Count;
        int start   = _hot.Cursor;
        bool didOne = false;
        var removeAfter = new List<long>();

        for (int i = 0; i < count; i++)
        {
            // After the first chunk honour the budget so hot-phase doesn't
            // consume all of remaining (worst case: 64 chunks × ~4 MB = 268 MB).
            if (didOne && remaining <= 0) break;

            int idx        = (start + i) % count;
            long chunkStart = hotList[idx];
            int read = _reader.Read(chunkStart, out int lookback, out int searchable);
            if (read == 0) { removeAfter.Add(chunkStart); continue; }

            onChunk(_reader.Buf, lookback, searchable, chunkStart - lookback);
            remaining -= read;
            didOne     = true;
            // Advance the rotation cursor so the next pass picks up where this one left off.
            _hot.NoteProcessed(idx, count);
        }

        foreach (long addr in removeAfter) _hot.Remove(addr);
        return remaining;
    }

    private void StartGeneration(long now)
    {
        _generation++;
        _genStartTime = now;
        _genComplete  = false;
        _cursor       = 0;
    }
}
