using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The resumable, byte-budgeted whole-process pool-region walk that PoolLocator drives. It keeps an
/// address cursor stepped a bounded number of bytes per <see cref="Step"/> so the walk spans many Engine ticks
/// (the old synchronous LocateAll took 7 to 10 seconds on the background loop). The cursor is not
/// <see cref="ScanCursor"/>: a region index goes stale because the region list grows and expands mid-scan
/// (seen across eight locates in one battle). An address cursor with a periodically refreshed
/// <see cref="ChunkReader.Snapshot"/> (<see cref="SnapshotRefreshMs"/>) only asks for work at or after
/// an address, so late regions are still picked up. Every qualifying region is staged and handed back only in
/// the Complete result, so a partial scan is never published.
/// </summary>
internal sealed class PoolScan
{
    /// <summary>One Step's outcome. Regions is empty until Complete is true.</summary>
    internal readonly record struct StepResult(bool Complete,
        IReadOnlyList<(long baseAddr, long size)> Regions, int Ticks, long Bytes, long ElapsedMs);

    private readonly CardPatterns _pats;
    private readonly ChunkReader _reader;

    /// <summary>Minimum gap between region snapshots (retune, tape 2026-08-18). One VirtualQueryEx walk costs
    /// about 45ms (5519 regions, 1362 kept; tools/probes/vq_walk_cost.py). Snapshotting every Step cost about 29s
    /// (32 percent) of a 92.2s scan (647 ticks). A region committed inside the window can be missed by this
    /// scan; the RevalidateMs reverify and the next scan pick it up.</summary>
    internal const long SnapshotRefreshMs = 1000;

    private bool _running;
    private long _cursor;
    private long _startMs;
    private int _ticks;
    private long _bytesRead;
    private long _lastSnapshotMs = long.MinValue / 2;   // sentinel; Begin overwrites it
    private readonly List<(long baseAddr, long size)> _staging = new();

    // Region being accumulated; a region larger than one budget resumes across Steps, so its id set persists.
    private long _curRegionBase = -1;
    private long _curRegionEnd;
    private readonly HashSet<int> _curIds = new();

    public PoolScan(IGameMemory mem, CardPatterns pats)
    {
        _pats = pats;
        _reader = new ChunkReader(mem);
    }

    /// <summary>True from <see cref="Begin"/> until the walk reports Complete.</summary>
    public bool Running => _running;

    /// <summary>Start a fresh walk from the beginning of the address space, discarding any
    /// in-progress staging from a prior scan.</summary>
    public void Begin(long nowMs)
    {
        _running = true;
        _cursor = 0;
        _startMs = nowMs;
        _ticks = 0;
        _bytesRead = 0;
        _staging.Clear();
        _curRegionBase = -1;
        _curRegionEnd = 0;
        _curIds.Clear();
        _reader.Snapshot();          // fresh at scan start, ignoring the cadence
        _lastSnapshotMs = nowMs;
    }

    /// <summary>Advance the walk by at most budgetBytes (plus the one chunk that guarantees progress) and
    /// report whether it finished. Re-snapshots only after <see cref="SnapshotRefreshMs"/>.</summary>
    public StepResult Step(long budgetBytes, long nowMs)
    {
        _ticks++;
        if (nowMs - _lastSnapshotMs >= SnapshotRefreshMs)
        {
            _reader.Snapshot();
            _lastSnapshotMs = nowMs;
        }
        long remaining = budgetBytes;
        bool firstChunk = true;

        foreach (var (rbase, rsize) in _reader.Regions)
        {
            long rend = rbase + rsize;
            if (rend <= _cursor) continue;   // already covered

            if (rbase != _curRegionBase)
            {
                FinalizeCurrentRegion();
                _curRegionBase = rbase;
            }
            _curRegionEnd = rend;   // the region may have grown in place

            long chunkStart = Math.Max(rbase, _cursor);
            while (chunkStart < rend)
            {
                if (!firstChunk && remaining <= 0) { _cursor = chunkStart; return InProgress(); }
                firstChunk = false;

                int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
                if (read == 0) { chunkStart += ChunkReader.ChunkSize; continue; }

                var scan = PoolLocatorPolicy.Scan(_reader.Buf, lookback, searchable, _pats);
                foreach (var h in scan.Hits) _curIds.Add(h.Id);

                _bytesRead += read;
                remaining -= read;
                chunkStart += ChunkReader.ChunkSize;
            }
            // _cursor is set only on the budget-exhausted return above. Advancing it here would skip the
            // next region whenever a gap separates it from this one; a stale low cursor is harmless because
            // regions are ascending and non-overlapping.
        }

        // Every region in this snapshot has been consumed.
        FinalizeCurrentRegion();
        _running = false;
        return new StepResult(true, _staging, _ticks, _bytesRead, nowMs - _startMs);
    }

    private StepResult InProgress() => new(false, Array.Empty<(long, long)>(), _ticks, _bytesRead, 0);

    /// <summary>Stage the region the cursor just left if it attributed a distinct weapon id
    /// (PoolLocator.ScanRegion's isPool rule), then reset the accumulator.</summary>
    private void FinalizeCurrentRegion()
    {
        if (_curRegionBase < 0) return;
        if (_curIds.Count > 0) _staging.Add((_curRegionBase, _curRegionEnd - _curRegionBase));
        _curRegionBase = -1;
        _curIds.Clear();
    }
}
