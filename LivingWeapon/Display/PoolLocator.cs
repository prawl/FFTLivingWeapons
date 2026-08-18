using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Locates (and caches) the writable UE string pool region(s) the equip card re-materializes
/// its description from (LW-37). Mirrors GrowthEngine.Locate's cache-or-rescan template: a
/// cached set is re-verified in place before it is trusted; a miss (or no cache yet) rescans
/// every region IGameMemory.Regions() yields, keeping each that PoolLocatorPolicy scores as pool.
///
/// Why ALL of them, not the single best (live finding 2026-07-08): the process holds SEVERAL
/// name-bearing baked copies of the item descriptions (a low-address working table plus the
/// high-address table the card actually materializes from), each attributing all 121 living
/// weapons. There is no static byte signature for WHICH one the card reads, so picking the
/// "most distinct / first" one painted the wrong copy and left the card baked. PoolPaint instead
/// paints EVERY qualifying region: they are few and small, and every write is name-gated,
/// foreign-refused, and Writable-checked, so painting a non-source baked copy is harmless.
///
/// Correctness invariant (build-plan B1): Regions() yields ONLY committed/PRIVATE/writable
/// memory (Mem.cs), so any region located is writable BY CONSTRUCTION: a read-only pool is never
/// in Regions() at all, so a scan simply omits it (the caller keeps sweeping) rather than
/// reporting a write target that cannot be painted.
///
/// PREMISE STATUS: that the specific named pool this class locates is the copy the card actually
/// materializes from is docs/LIVE_LEDGER.md's [card-materializes-from-named-pool] row -- filed
/// Uncertain (commit/changelog grade, one below ledger-PROVEN), owner-only flip. LW-261 does not
/// touch this premise at all; it matters only for a targeted per-region drain check that is
/// explicitly out of this arc's scope.
///
/// LW-261: the region walk itself is now resumable and byte-budgeted (<see cref="PoolScan"/>),
/// replacing a synchronous full-process walk that a live measurement clocked at 7 to 10 seconds
/// on the mod's single 33ms background loop. <see cref="Step"/> is the production entry point --
/// Engine's own "pool-locate" tick lane (Display.PoolLocate.cs's StepPoolLocate) drives it every
/// tick, independent of whether the equip card happens to be paintable this tick, and
/// Display.PoolPaint.cs's MaybePoolPaint also drives it on its own fall-through so a bare Display
/// exercised only through repeated Tick() calls (every existing unit test) still makes progress
/// on its own. <see cref="CachedRegions"/> publishes ONLY on a scan's completion, never a partial
/// result; <see cref="Invalidate"/> queues a restart rather than clearing the cache outright, so a
/// mid-flight scan is never aborted and CachedRegions keeps serving the last good list (marked
/// <see cref="RegionsStale"/>) until the queued restart republishes. <see cref="LocateAll"/>
/// remains as a synchronous drive-to-completion convenience: production no longer calls it (the
/// whole point of this arc is that nothing should block a tick waiting for a full scan), but it
/// keeps every pre-existing test that calls it directly working unchanged.
///
/// FILE SPLIT (round 2 verify B4, revised round 3 C5): this file holds the plain cache surface --
/// CachedRegions, SeedForTest, the cheap AllCachedStillPool/ScanRegion reverify, and the
/// test-only synchronous LocateAll. PoolLocator.Restart.cs holds the restart/cadence/staleness
/// state machine that decides WHEN to (re)scan and what RegionsStale/trigger a caller sees while
/// that decision plays out -- Step, Invalidate, AND Publish (moved there in round 3: the single
/// most contested line in the whole state machine, Publish's `_stale = _restartPending`, needs to
/// sit beside the field it writes and the method whose queued restart it has to outlive, not in
/// this file). A genuinely different concern (timing and trust vs. plain data), not a split of
/// one state machine across two files.
/// </summary>
internal sealed partial class PoolLocator
{
    /// <summary>The resumable scan's own per-CALL byte lane, separate from Display.Tick's own
    /// BudgetInBattle/BudgetOutOfBattle budgets so this scan's cost stays bounded independent of
    /// whatever else is sharing the tick. NOT a per-REAL-ENGINE-TICK cap: Display.PoolPaint.cs's
    /// MaybePoolPaint and Engine's own "pool-locate" phase both call Display.StepPoolLocate (that
    /// file's own class doc explains why), and both CAN land in the same real tick, so the true
    /// worst case per real tick is roughly twice this constant, not once (pinned by
    /// DisplayPoolLocateBudgetTests.Two_same_tick_call_sites_never_exceed_roughly_twice_the_locate_budget).
    /// A FIRST ESTIMATE, not a measurement: half of Display's in-battle sweep budget, picked
    /// before any live data existed on how many ticks or how long a resumable scan actually
    /// takes. Retune this from the ticks/bytes/ms the "LW37 locate-complete" line and the
    /// "locate-complete" flight record (Display.PoolLocate.cs) report on the first live pass
    /// after this ships -- but NOT downward: this constant currently EQUALS ChunkReader.ChunkSize,
    /// and PoolScan.Step always reads at least one whole chunk to guarantee forward progress
    /// (its own doc), so any value at or below ChunkSize reads exactly one chunk per call either
    /// way. A downward retune is a no-op until this is raised above ChunkSize first.</summary>
    internal const long LocateBudgetBytes = 4L * 1024 * 1024;

    private readonly CardPatterns _pats;
    private readonly ChunkReader _reader;      // AllCachedStillPool's own reader -- the cheap cached-region reverify, never the full walk
    private readonly PoolScan _scan;           // the resumable full walk (LW-261)
    private readonly Func<long> _nowMs;

    private readonly List<(long baseAddr, long size)> _cached = new();

    public PoolLocator(IGameMemory mem, CardPatterns pats, Func<long>? nowMs = null)
    {
        _pats = pats;
        _reader = new ChunkReader(mem);
        _scan = new PoolScan(mem, pats);
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>TEST-ONLY. Production must NEVER call this: it blocks the calling thread until the
    /// WHOLE resumable scan finishes, which is exactly the 7-to-10-second freeze this arc exists to
    /// remove (this class's own opening doc). The only reason it still exists is so every
    /// pre-existing test that calls LocateAll directly keeps its exact cache-or-rescan behavior,
    /// now with the resumable scan underneath instead of a plain loop -- a future caller reaching
    /// for "just locate it synchronously" here reintroduces the original bug; call <see
    /// cref="Step"/> from a tick instead (Display.PoolLocate.cs's StepPoolLocate is the real entry
    /// point). Reuses the same short-circuit LocateAll always had (a cache whose every region still
    /// reads as pool is returned without a rescan), now ALSO refusing the short-circuit while a
    /// restart is queued (an Invalidate() or a failed revalidate must still force the next call
    /// here to rescan).</summary>
    internal IReadOnlyList<(long baseAddr, long size)> LocateAll()
    {
        if (_cached.Count > 0 && !_restartPending && AllCachedStillPool()) return _cached;

        _pendingTrigger = _cached.Count == 0 ? "first" : "invalidate";
        _restartPending = false;
        _scan.Begin(_nowMs());
        PoolScan.StepResult result;
        do { result = _scan.Step(LocateBudgetBytes, _nowMs()); } while (!result.Complete);

        Publish(result.Regions, _nowMs());
        return _cached;
    }

    /// <summary>Re-verify each cached region still reads as pool (relocation / realloc guard).</summary>
    private bool AllCachedStillPool()
    {
        foreach (var r in _cached) if (!ScanRegion(r.baseAddr, r.size).isPool) return false;
        return true;
    }

    /// <summary>The CURRENTLY published region list, exactly as of the last completed scan -- a
    /// plain accessor, no scan of its own, no Regions() walk. Empty whenever no scan has ever
    /// completed yet (every poolPaint:false test, and the pool path before its first successful
    /// locate): Display.Heartbeat.cs's pending-set clear predicate reads this (together with <see
    /// cref="RegionsStale"/>) to tell "nothing trustworthy known yet" apart from "a currently
    /// located, non-stale region set", and Display.PoolPaint.cs's per-region drain latch keys off
    /// the same list.</summary>
    internal IReadOnlyList<(long baseAddr, long size)> CachedRegions => _cached;

    /// <summary>Test-only cache seed (mirrors GrowthEngine.SeedStructForSlotForTest): drives the
    /// revalidate path directly against pinned regions, without needing a prior scan.</summary>
    internal void SeedForTest(params (long baseAddr, long size)[] regions)
    {
        _cached.Clear();
        _cached.AddRange(regions);
        _restartPending = false;
        _stale = false;
    }

    /// <summary>Aggregate PoolLocatorPolicy.Scan across every chunk of [rbase, rbase+rsize)
    /// via ChunkReader (not a slack-less whole-region read: a real pool can exceed
    /// ChunkSize). IsPool and the distinct-weapon count union across chunks. Used only by
    /// AllCachedStillPool's cheap per-region reverify -- the full walk lives in PoolScan.</summary>
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
