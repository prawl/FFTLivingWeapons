using System;
using System.Collections.Generic;
using System.Linq;

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
/// Hot-chunk lifetime: every MarkHot refreshes the chunk's timestamp; chunks not re-marked
/// within HotTtlMs are silently dropped so a region that stops yielding hits drains out of
/// the hot set automatically.
/// </summary>
internal sealed partial class DisplaySweep
{
    public delegate void ChunkHandler(byte[] buf, int lookback, int searchable, long bufBaseAddr);

    public const int ChunkSize           = 4 * 1024 * 1024;
    public const int Lookback            = 4096;
    public const int TrailSlack          = 64;
    public const long HotRescanMs        = 250;
    public const long GenerationRestMs   = 90_000;
    public const long GenerationMinGapMs = 5_000;
    public const int MaxHotChunks        = 64;
    public const long HotTtlMs           = 10_000;

    private readonly IGameMemory _mem;
    private readonly Func<long> _nowMs;
    private readonly byte[] _buf = new byte[Lookback + ChunkSize + TrailSlack];

    // generation state
    private long _generation;
    private long _genStartTime   = long.MinValue / 2;
    private long _genCompleteTime = long.MinValue / 2;
    private bool _genComplete    = false;
    private bool _invalidatePending = false;

    // background walk cursor
    private long _cursor = 0;

    // region snapshot reused each Tick to avoid one VirtualQueryEx walk per hot chunk
    internal readonly List<(long rbase, long rsize)> _regionSnap = new();

    // hot chunks: chunk-aligned address -> last MarkHot timestamp (ms)
    // Eviction is LRU (oldest lastMarked) when count exceeds MaxHotChunks.
    private readonly Dictionary<long, long> _hotChunks = new();

    // rotation cursor: index into the hot-chunk list at which the hot pass resumes
    // so no chunk is permanently starved when the budget allows fewer than MaxHotChunks.
    private int _hotCursor = 0;

    // hot phase timing -- sentinel far enough back to be "overdue" at t=0
    private long _lastHotPassMs = long.MinValue / 2;
    private bool _rescanRequested = false;

    public DisplaySweep(IGameMemory mem, Func<long> nowMs)
    {
        _mem   = mem;
        _nowMs = nowMs;
        StartGeneration(nowMs());
    }

    /// <summary>Increments when a new full pass starts.</summary>
    public long Generation => _generation;

    /// <summary>True once the current generation has exhausted all regions.</summary>
    public bool IsComplete => _genComplete;

    /// <summary>Record that a chunk at the given address yielded paint sites; keep it in
    /// the hot set and refresh its TTL so live chunks never expire.
    /// Resets the hot-rescan countdown so the consumer gets HotRescanMs before the next pass.</summary>
    public void MarkHot(long chunkStart)
    {
        bool wasEmpty = _hotChunks.Count == 0;
        long now = _nowMs();
        _hotChunks[chunkStart] = now;

        // Evict the chunk with the oldest lastMarked timestamp when over the cap.
        while (_hotChunks.Count > MaxHotChunks)
        {
            long oldest = _hotChunks.MinBy(kv => kv.Value).Key;
            _hotChunks.Remove(oldest);
        }

        // Start the countdown from this mark so caller gets a full HotRescanMs before re-offer.
        if (wasEmpty) _lastHotPassMs = now;
    }

    /// <summary>A count or target change: rescan hot chunks on the next Tick regardless of clock.</summary>
    public void RequestRescan() => _rescanRequested = true;

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
        _regionSnap.Clear();
        foreach (var r in _mem.Regions()) _regionSnap.Add(r);

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
        foreach (var (rbase, rsize) in _regionSnap)
        {
            long rend = rbase + rsize;
            if (rend <= _cursor) continue; // region entirely before cursor
            long chunkStart = Math.Max(rbase, _cursor);

            while (chunkStart < rend)
            {
                if (!firstChunk && remaining <= 0) { _cursor = chunkStart; return; }
                firstChunk = false;

                int read = ReadChunkInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
                if (read == 0) { chunkStart += ChunkSize; continue; }

                onChunk(_buf, lookback, searchable, chunkStart - lookback);
                remaining -= read;
                chunkStart += ChunkSize;
            }
        }

        // Exhausted all regions: generation complete.
        _cursor          = 0;
        _genComplete     = true;
        _genCompleteTime = now;
    }

    private void StartGeneration(long now)
    {
        _generation++;
        _genStartTime = now;
        _genComplete  = false;
        _cursor       = 0;
    }
}
