using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-261 round 2 verify (B4), Publish added round 3 (C5): the restart/cadence/staleness state
/// machine -- WHEN to (re)scan, what a caller sees (RegionsStale, the completion's trigger) while
/// that decision plays out, and the one write that actually clears staleness (Publish). Split out
/// of PoolLocator.cs once the resumable-scan rewrite plus the B1/B2 straddled-invalidate fixes
/// pushed that file to 305 lines (round 3 correction, C6: MOSTLY comment prose documenting those
/// fixes, not code growth -- do not read "past the 200-line trigger" as "the logic got more
/// complex"; PoolLocator.cs itself is back to 186 lines after this split, most of it doc comments
/// too). Genuinely a different concern from PoolLocator.cs's own (what the cache holds vs. WHEN
/// and how it may change): Step, Invalidate, and Publish, the three methods that actually drive
/// this timing state, all stay whole here rather than being cut across files.
/// </summary>
internal sealed partial class PoolLocator
{
    /// <summary>Cadence for AllCachedStillPool's relocation/realloc reverify of the currently
    /// published regions, driven from <see cref="Step"/>. Matches Display.MaintenanceMs (also
    /// 1000ms) -- there is no evidence either way that a tighter or looser cadence matters more,
    /// and sharing the number keeps the two once-a-second background checks easy to reason about
    /// together on a tape.</summary>
    internal const long RevalidateMs = 1000;

    /// <summary>Round 3 verify (C1): NOT an anomaly threshold, and the constant this replaces
    /// (WatchdogTicks = 90) was miscalibrated on top of being framed wrong. That old comment
    /// justified 90 ticks against ~145MB, the FOUND POOL REGIONS this arc measured live -- but
    /// PoolScan walks every committed writable region Mem.Regions() yields, the WHOLE process
    /// heap, an unrelated and far larger quantity nothing here has ever measured. Since
    /// LocateBudgetInBattle equals ChunkReader.ChunkSize, the at-least-one-chunk floor meant
    /// exactly one chunk per in-battle Step, so a real scan is many hundreds to thousands of Steps -- 90 would have
    /// fired on essentially every scan, a false anomaly signature on the very tape a live pass
    /// reads. Rather than guess a second threshold with the same failure mode, this now logs
    /// PURE PROGRESS on a fixed cadence (no "something is wrong" claim at all) so the owner's
    /// first live tape can finally show what a real completion tick count looks like; retune
    /// (or reinstate a real anomaly threshold, once one can be derived from that tape) from those
    /// numbers.
    ///
    /// Internal, not private (LW-267 pin, zero behavior change): lets PoolLocatorGuardTests
    /// name this constant in its doc cref. The pin itself deliberately hardcodes the literal
    /// 300 instead of reading this symbol, so a mutated constant cannot silently retune the
    /// test along with the code (the pin's first draft did read it, and was vacuous for exactly
    /// that reason). Mirrors the test-accessor convention Display.Flight.cs documents.</summary>
    internal const int ProgressLogEveryTicks = 300;   // 1.2GB/tick-count at LocateBudgetInBattle (4.8GB out of battle, LocateBudgetOutOfBattle); a readable cadence, not a measured one

    internal readonly record struct LocateCompletion(int Regions, int Ticks, long Bytes, long ElapsedMs, string Trigger);

    // Restart coalescing (LW-261): Invalidate() sets these instead of clearing _cached outright
    // (round 3 verify, C3(a): a failed revalidate does NOT set _restartPending -- it runs only
    // while no scan is in flight, so it falls straight through to _scan.Begin in the SAME Step
    // call, never through the queued path _restartPending exists for). A scan already running is
    // never touched by Invalidate() either -- the restart is queued and taken up only once Step
    // next finds _scan.Running false, so any number of Invalidate() calls landing mid-scan
    // collapse into exactly one eventual restart (the livelock fix: this mod's own battle edges
    // fire several Invalidates per battle, and an abort-on-invalidate design could never let a
    // scan finish under that load).
    private bool _restartPending;
    private bool _stale = true;   // true until the very first publish; RegionsStale's backing field
    private string _pendingTrigger = "first";
    // B2 (round 2 verify): captured from _pendingTrigger at the moment _scan.Begin actually runs,
    // so a mid-flight Invalidate() overwriting _pendingTrigger (queuing the NEXT restart's own
    // reason) can never bleed into the trigger reported for the run already in flight.
    private string _runningTrigger = "first";
    private bool _hasScannedOnce;   // true once ANY scan has completed, found something or not -- distinguishes "first" from "empty-retry"

    private long _lastRevalidateMs = long.MinValue / 2;   // sentinel: DisplaySweep.cs's own idiom, guarantees the first check is always "due" regardless of what nowMs starts at
    internal int _revalidateCount;   // test-only: how many times the Step-driven revalidate cadence actually fired

    /// <summary>True from an <see cref="Invalidate"/> (or a failed periodic revalidate) until the
    /// next publish. Display.Heartbeat.cs's ProcessPending reads this: a stale-but-non-empty cache
    /// is exactly as untrustworthy as an empty one for judging whether a pending kill settled at a
    /// site inside a "currently located" region.</summary>
    internal bool RegionsStale => _stale;

    /// <summary>Commit 1B: bumped on every <see cref="Publish"/> (and <see cref="SeedForTest"/>,
    /// PoolLocator.cs), never reset. Display.PoolPaint.cs's MaybePoolPaint compares this against
    /// the generation it last actually ran ScanPoolRegion against, so a genuinely fresh region
    /// list (a completed locate, revalidate-triggered rescan, or straddled-invalidate republish)
    /// is always scanned immediately rather than waiting out the maintenance cadence -- "a fresh
    /// publish from the locate" is one of the two genuine triggers that commit's own doc names.
    /// A plain int, not a bool: CachedRegions' own contents can be mutated in place by Publish
    /// (Clear+AddRange on the SAME List), so reference identity cannot signal a change the way it
    /// would for an immutable snapshot.</summary>
    internal int PublishGeneration { get; private set; }

    /// <summary>Drive the resumable scan by up to <paramref name="budgetBytes"/> THIS call, or run
    /// the cheap periodic revalidate of the already-published regions when nothing else is due.
    /// The production entry point (Display.PoolLocate.cs's StepPoolLocate, called every Engine
    /// tick on the dedicated "pool-locate" lane, and Display.PoolPaint.cs's MaybePoolPaint on its
    /// own fall-through) -- see this class's own doc for why both call it.
    ///
    /// Publishes into <see cref="CachedRegions"/> ONLY on the tick a scan actually completes,
    /// never a partial result (PoolScan's own contract). Returns a non-null <see
    /// cref="LocateCompletion"/> exactly on that tick, so a caller with a flight-recorder tap
    /// knows precisely when to fire it -- every other call (nothing due, or a scan still
    /// in-flight) returns null.</summary>
    internal LocateCompletion? Step(long budgetBytes, long nowMs)
    {
        if (!_scan.Running)
        {
            // Verify round FIX 1: this cadence gate used to sit ONLY inside the `_cached.Count > 0`
            // branch, so a scan that completed having found NOTHING (no pool region anywhere yet,
            // e.g. cold boot before the pool has been allocated) had no cadence at all -- Step
            // restarted a brand-new scan on the very next call, forever, at LocateBudgetInBattle/
            // LocateBudgetOutOfBattle per tick on the Always lane. The cadence now gates BOTH shapes identically: whether the
            // cache is populated (revalidate it) or empty (retry a fresh scan), neither may run
            // again before RevalidateMs has elapsed, unless a restart is explicitly queued
            // (Invalidate() or a failed revalidate from a PRIOR call, both of which bypass this
            // wait deliberately -- a queued restart must not itself wait out the cadence).
            if (!_restartPending)
            {
                if (nowMs - _lastRevalidateMs < RevalidateMs) return null;   // not due yet
                _lastRevalidateMs = nowMs;

                if (_cached.Count > 0)
                {
                    _revalidateCount++;
                    long t0 = _nowMs();
                    bool stillPool = AllCachedStillPool();
                    LogRevalidate(_nowMs() - t0, stillPool);
                    if (stillPool) return null;   // still good: nothing to do this tick
                    _pendingTrigger = "revalidate";
                }
                else
                {
                    _pendingTrigger = _hasScannedOnce ? "empty-retry" : "first";
                }
            }
            // Round 3 verify (C5): evaluated for redundancy against Publish's own `_stale =
            // _restartPending` (B1). NOT redundant in general -- only for the restart-pending
            // pickup path just above (where Invalidate()/a straddled Publish already guarantee
            // _stale is true). The other two paths through the branch above (a revalidate that
            // just found the cache stale, or an empty-retry/first attempt) both follow a PRIOR
            // Publish that ran with _restartPending false, which cleared _stale to false -- so
            // without this unconditional line, staleness would read false while THIS fresh scan
            // is still in flight. Kept unconditional rather than folded into just those two
            // branches: one line covering all three paths reads clearer than duplicating it.
            _stale = true;
            _restartPending = false;
            _runningTrigger = _pendingTrigger;   // B2: snapshot BEFORE any later Invalidate() can move _pendingTrigger out from under this run
            _scan.Begin(nowMs);
        }

        var result = _scan.Step(budgetBytes, nowMs);
        if (!result.Complete)
        {
            if (result.Ticks % ProgressLogEveryTicks == 0)
            {
                LogProgress(result.Ticks, result.Bytes);
            }
            return null;
        }

        Publish(result.Regions, nowMs);
        LogLocateComplete(result);
        return new LocateCompletion(_cached.Count, result.Ticks, result.Bytes, result.ElapsedMs, _runningTrigger);
    }

    /// <summary>Queue a restart rather than clearing the cache outright (LW-261): a scan already
    /// running is untouched (never aborted -- the livelock fix, this class's own doc), and
    /// CachedRegions keeps serving the last good list, marked <see cref="RegionsStale"/>, until the
    /// queued restart republishes. Call on battle exit or any event that reallocates the menu's
    /// render buffers.</summary>
    internal void Invalidate()
    {
        _restartPending = true;
        _stale = true;
        _pendingTrigger = "invalidate";
    }

    /// <summary>Round 3 verify (C5): moved here from PoolLocator.cs so the single most contested
    /// line in this whole state machine -- the B1 fix `_stale = _restartPending` below -- lives
    /// beside the field it writes (_stale) and the method (Invalidate) whose queued restart it
    /// has to outlive, instead of in the OTHER file. Still the one place a fresh scan result
    /// actually gets written into the cache (PoolLocator.cs's own CachedRegions/SeedForTest stay
    /// there, since owning "what the cache holds" is that file's job); this method is the bridge
    /// between the two concerns, and belongs with the state machine that decides WHEN it may
    /// safely clear staleness, not with the plain data it writes.</summary>
    private void Publish(IReadOnlyList<(long baseAddr, long size)> regions, long nowMs)
    {
        _cached.Clear();
        _cached.AddRange(regions);
        // B1 (round 2 verify): NOT an unconditional clear. A scan already running when
        // Invalidate() lands straddles the relocation and still publishes here on its own (a
        // mid-flight scan is never aborted, this class's own doc) -- but if a restart is STILL
        // queued at this exact moment (_restartPending true), this result is exactly the
        // straddled one Invalidate() distrusted, so staleness must survive this publish. Only
        // the publish that finds NO restart still owed may actually clear it -- which is also
        // correct for the queued restart's own later publish, since Step clears _restartPending
        // the moment it picks the restart up, long before that scan finishes.
        _stale = _restartPending;
        _hasScannedOnce = true;
        // Verify round FIX 6: stamp the revalidate clock HERE, at the moment the fresh set is
        // actually published, not just at whatever earlier tick decided to start this scan. A
        // scan can take many ticks to complete (the live tape saw 52 to 145MB regions; at
        // LocateBudgetInBattle/tick that alone can exceed RevalidateMs), so leaving the clock at its
        // scan-START stamp meant a second call site (MaybePoolPaint's own fall-through, REMOVED by
        // this same retune round's R1 -- past tense now, but the guard below is still correct and
        // still tested directly, since any future caller could still Step twice in one tick)
        // landing on the SAME tick the scan finished would have read the cadence as long overdue
        // and run a full unbudgeted AllCachedStillPool over the just-published regions on the spot
        // -- exactly the per-tick cost this whole arc exists to bound.
        _lastRevalidateMs = nowMs;
        PublishGeneration++;   // commit 1B: every publish is a "fresh region list" event
        LogRegionsFound();
    }
}
