using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Locates and caches the writable string pool regions the equip card rebuilds its description from.
/// A cached set is re-verified in place; a miss rescans every region IGameMemory.Regions() yields and keeps each one PoolLocatorPolicy scores as pool.
/// All qualifying regions are kept, not the best one (2026-07-08): several baked copies of the descriptions exist and no static signature says which the card reads. Writes are name-gated and Writable-checked, so an extra copy is harmless.
/// Regions() yields only committed private writable memory, so a located region is writable. That the card reads this named pool is docs/LIVE_LEDGER.md [card-materializes-from-named-pool], filed Uncertain.
/// The scan is resumable and byte-budgeted (<see cref="PoolScan"/>), replacing a 7 to 10 second walk on the 33ms loop. <see cref="Step"/> is driven by Engine's pool-locate lane (Display.PoolLocate.cs StepPoolLocate). <see cref="CachedRegions"/> publishes only on completion; <see cref="Invalidate"/> queues a restart and the last list stays served, marked <see cref="RegionsStale"/>. <see cref="LocateAll"/> is a synchronous test helper.
/// PoolLocator.Restart.cs holds the restart, cadence and staleness state (Step, Invalidate, Publish); this file holds the cache and the cheap reverify.
/// </summary>
internal sealed partial class PoolLocator
{
    /// <summary>Per-Step byte budget in battle. One ChunkReader.ChunkSize chunk, to keep the loop near 33ms (cf. Display's BudgetInBattle).
    /// Retune 2026-08-18. The old per-call snapshot logged 100.5 to 157.7ms per tick; dropping one ~45ms snapshot per Step (tools/probes/vq_walk_cost.py) predicts 36 to 73s per scan.
    /// Measured that day 72.2s and 88.4s.</summary>
    internal const long LocateBudgetInBattle = 4L * 1024 * 1024;

    /// <summary>Same lane out of battle, four chunks per Step (Display's BudgetOutOfBattle is also 16MB).
    /// Measured 2026-08-18. The cold-boot scan took 29.8s over 151 Steps (~197ms each), about double the predicted 85 to 90ms because the heap sweep shares those ticks.</summary>
    internal const long LocateBudgetOutOfBattle = 16L * 1024 * 1024;

    private readonly CardPatterns _pats;
    private readonly ChunkReader _reader;      // reader for the cached-region reverify
    private readonly PoolScan _scan;           // the resumable full walk
    private readonly Func<long> _nowMs;

    private readonly List<(long baseAddr, long size)> _cached = new();

    /// <param name="regionSidecarPath">pool_regions.json path (SaveLocation.PathFor) for the warm-start seed (PoolLocator.WarmStart.cs SeedFromSidecar) and write-on-completion (MaybeSaveSidecar). Null disables both.</param>
    public PoolLocator(IGameMemory mem, CardPatterns pats, Func<long>? nowMs = null, string? regionSidecarPath = null)
    {
        _pats = pats;
        _reader = new ChunkReader(mem);
        _scan = new PoolScan(mem, pats);
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _regionSidecarPath = regionSidecarPath;
    }

    /// <summary>Test only. Blocks until the whole scan finishes, the freeze LW-261 removed; production calls <see cref="Step"/> from a tick (Display.PoolLocate.cs StepPoolLocate).
    /// Returns the cache without rescanning when every region still reads as pool and no restart is queued.</summary>
    internal IReadOnlyList<(long baseAddr, long size)> LocateAll()
    {
        if (_cached.Count > 0 && !_restartPending && AllCachedStillPool()) return _cached;

        _pendingTrigger = _cached.Count == 0 ? "first" : "invalidate";
        _restartPending = false;
        _scan.Begin(_nowMs());
        PoolScan.StepResult result;
        do { result = _scan.Step(LocateBudgetInBattle, _nowMs()); } while (!result.Complete);

        Publish(result.Regions, _nowMs());
        return _cached;
    }

    /// <summary>Re-verify each cached region still reads as pool (relocation / realloc guard).</summary>
    private bool AllCachedStillPool()
    {
        foreach (var r in _cached) if (!ScanRegion(r.baseAddr, r.size).isPool) return false;
        return true;
    }

    /// <summary>The region list from the last completed scan; no scan of its own. Empty until the first scan completes. Display.Heartbeat.cs's pending-set clear and Display.PoolPaint.cs's drain latch read it.</summary>
    internal IReadOnlyList<(long baseAddr, long size)> CachedRegions => _cached;

    /// <summary>Test-only cache seed; exercises the revalidate path against pinned regions without a prior scan.</summary>
    internal void SeedForTest(params (long baseAddr, long size)[] regions)
    {
        _cached.Clear();
        _cached.AddRange(regions);
        _restartPending = false;
        _stale = false;
        PublishGeneration++;   // commit 1B. A seed counts as a fresh region list
    }

    /// <summary>Aggregates PoolLocatorPolicy.Scan across the chunks of [rbase, rbase+rsize) via ChunkReader, since a pool can exceed ChunkSize.
    /// Used by AllCachedStillPool's reverify; the full walk is PoolScan.</summary>
    private (bool isPool, int distinct) ScanRegion(long rbase, long rsize)
    {
        var ids = new HashSet<int>();
        long rend = rbase + rsize;
        long chunkStart = rbase;
        while (chunkStart < rend)
        {
            int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
            if (read == 0) break;
            var scan = PoolLocatorPolicy.Scan(_reader.Buf, lookback, searchable, _pats);
            foreach (var h in scan.Hits) ids.Add(h.Id);
            chunkStart += ChunkReader.ChunkSize;
        }
        return (ids.Count > 0, ids.Count);
    }
}
