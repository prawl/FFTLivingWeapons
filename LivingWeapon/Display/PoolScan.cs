using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-261: the resumable, byte-budgeted whole-process pool-region walk PoolLocator drives. What
/// <see cref="DisplaySweep"/> is to Display's own paint pipeline, this is to PoolLocator: a
/// composed class (not a partial -- there is no shared state to split, just one self-contained
/// walk), owning an ADDRESS cursor stepped a bounded number of bytes per <see cref="Step"/> call
/// so it can resume across many Engine ticks instead of blocking one tick for the whole walk (a
/// live measurement clocked the old synchronous PoolLocator.LocateAll at 7 to 10 seconds on this
/// exact background loop).
///
/// Cursor choice, and why it is deliberately NOT <see cref="ScanCursor"/>: ScanCursor's
/// RegionCursor carries a region INDEX, meaningful only against the frozen list it was built
/// from. A live measurement (eight successive locates inside one battle) proved the region list
/// is not stable during a scan -- it grows, gains new regions, and existing regions expand in
/// place, all within a single multi-second walk. Reusing that cursor here would import the exact
/// bug the measurement predicts: a region-index cursor silently resolves against whatever list
/// happens to be current when it is dereferenced, which can drift out from under an index taken
/// many ticks earlier. An address cursor plus a periodically refreshed <see
/// cref="ChunkReader.Snapshot"/> (round 5 verify, F2: NOT a fresh one every Step -- <see
/// cref="SnapshotRefreshMs"/>'s own doc has the cadence; DisplaySweep.cs's own idiom for its
/// background phase was per-call, this is not) still self-heals: it only ever asks "is there work
/// at or after this address", never "what is region N", so a region committed after the scan
/// started is picked up as of whichever Step is next due to refresh, as long as its extent
/// reaches at or past the cursor then.
///
/// Every qualifying region (not just the "best" one -- PoolLocator's own class doc explains why)
/// is staged as the walk finds it. The staging list is private to one in-flight scan and is
/// handed to the caller only in the Complete summary; a caller that publishes straight from a
/// Step result therefore can never publish a partial scan.
/// </summary>
internal sealed class PoolScan
{
    /// <summary>One Step's outcome. Regions is empty and meaningless while Complete is false --
    /// callers must never treat an in-progress result as a (possibly partial) region list.</summary>
    internal readonly record struct StepResult(bool Complete,
        IReadOnlyList<(long baseAddr, long size)> Regions, int Ticks, long Bytes, long ElapsedMs);

    private readonly CardPatterns _pats;
    private readonly ChunkReader _reader;

    /// <summary>Retune round (live tape 2026-08-18, owner pass): a full VirtualQueryEx region walk
    /// measured ~45ms against the running game process (5519 regions enumerated, 1362 kept; three
    /// trials 44.9 to 45.9ms cross-process -- tools/probes/vq_walk_cost.py is the tracked
    /// instrument, round 5 verify F7). Step used to re-snapshot on EVERY call, and R1 of this same
    /// retune round (the single production driver) had ALSO just removed the second call site
    /// that used to double that cost, so what the tape actually measured (six completions logged
    /// that day: 647/645/631/651/681/689 ticks, 65.4 to 101.7s wall clock, 100.5 to 157.7ms per
    /// Step) was the OLD every-call-snapshot design running through the SOLE remaining driver.
    /// Round 5 verify correction: that per-Step figure is NOT ~60 percent snapshot cost -- 647
    /// ticks x ~45ms is ~29.1s, about 32 percent of the 92.2s scan (about 29 percent of the 101.7s
    /// one); the rest is per-chunk read+scan plus whatever else shares the engine's own tick. THIS
    /// cadence is what actually removes most of that ~29 to 32 percent (R1 alone would not have --
    /// one driver instead of two means MORE ticks per scan, not fewer; R1's own win is per-tick
    /// latency and total CPU, not scan wall time). Region committed after a scan started is still
    /// eventually seen (a live region list changes on the order of 40 to 90 seconds between new
    /// pool regions on the same tape, never every second), just not necessarily within this window
    /// -- the tradeoff this constant makes explicit: a region committed just above the cursor
    /// inside the 1s window can be passed over THIS scan; the RevalidateMs periodic reverify and
    /// the next scan both heal it.</summary>
    internal const long SnapshotRefreshMs = 1000;

    private bool _running;
    private long _cursor;
    private long _startMs;
    private int _ticks;
    private long _bytesRead;
    private long _lastSnapshotMs = long.MinValue / 2;   // sentinel: DisplaySweep.cs's own idiom -- Begin() always overwrites this before it matters
    private readonly List<(long baseAddr, long size)> _staging = new();

    // The region currently being accumulated. A region larger than one Step's budget resumes
    // mid-region across many Step calls, so the distinct-weapon-id set for "the region the
    // cursor is inside right now" has to survive between calls, not just within one.
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
        _reader.Snapshot();          // ALWAYS fresh at the start of a scan, regardless of cadence
        _lastSnapshotMs = nowMs;
    }

    /// <summary>Advance the walk by at most budgetBytes (plus the one chunk DisplaySweep's own
    /// contract always spends to guarantee forward progress) and report whether it has finished.
    /// Re-snapshots Regions() only once <see cref="SnapshotRefreshMs"/> has elapsed since the last
    /// one (that constant's own doc has the live-measured cost this cadence exists to bound) --
    /// the self-heal described in the class doc still applies, just on that cadence rather than
    /// every call: a region committed since the last snapshot is visible here, as of whichever
    /// Step call is next due to refresh, as long as it reaches at or past the cursor.</summary>
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
            if (rend <= _cursor) continue;   // region entirely before the cursor: already covered

            if (rbase != _curRegionBase)
            {
                FinalizeCurrentRegion();
                _curRegionBase = rbase;
            }
            _curRegionEnd = rend;   // refresh even on a resumed region: it may have grown in place

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
            // Deliberately NOT `_cursor = chunkStart` here (DisplaySweep.cs's own background-phase
            // loop has this exact shape): _cursor is updated ONLY on the early-return budget-
            // exhausted branch above. Left stale after a region completes normally within this
            // same Step call, the NEXT region's own `Math.Max(rbase, _cursor)` still resolves
            // correctly (regions are ascending, non-overlapping, so a stale low cursor can never
            // wrongly skip a later region) -- updating it here instead would advance it past
            // whatever gap sits between this region and the next, so the very next region's own
            // `rend <= _cursor` guard would see a cursor already past its end and skip it outright.
        }

        // Every region visible in THIS snapshot has been consumed up to its own end: done.
        FinalizeCurrentRegion();
        _running = false;
        return new StepResult(true, _staging, _ticks, _bytesRead, nowMs - _startMs);
    }

    private StepResult InProgress() => new(false, Array.Empty<(long, long)>(), _ticks, _bytesRead, 0);

    /// <summary>Stage the region the cursor just left, if it attributed at least one distinct
    /// weapon id (PoolLocator.ScanRegion's own "isPool" rule), and reset the accumulator for
    /// whatever region comes next.</summary>
    private void FinalizeCurrentRegion()
    {
        if (_curRegionBase < 0) return;
        if (_curIds.Count > 0) _staging.Add((_curRegionBase, _curRegionEnd - _curRegionBase));
        _curRegionBase = -1;
        _curIds.Clear();
    }
}
