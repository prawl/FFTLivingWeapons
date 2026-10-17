using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The restart, cadence and staleness state machine for the pool locator. It decides
/// when to (re)scan, what callers see meanwhile (RegionsStale, the completion's trigger) and
/// which write clears staleness (Publish). Split from PoolLocator.cs because the
/// resumable-scan rewrite and the straddled-invalidate fixes pushed it past 200 lines, mostly
/// doc comments. Step, Invalidate and Publish drive the same timing state, so they stay together.
/// </summary>
internal sealed partial class PoolLocator
{
    /// <summary>Cadence for the relocation/realloc reverify of the published regions, driven from
    /// <see cref="Step"/>. Same 1000ms as Display.MaintenanceMs, so the two background checks line
    /// up on a tape.</summary>
    internal const long RevalidateMs = 1000;

    /// <summary>Progress log cadence in scan ticks. 300 is a round number, not a measured one;
    /// retune it, or derive a real anomaly threshold, from the first live tape's completion tick
    /// count. It is not an anomaly threshold. The old WatchdogTicks = 90 was calibrated against the
    /// ~145MB of found pool regions, but PoolScan walks the whole process heap, so a normal scan is
    /// hundreds to thousands of Steps and 90 would fire on every one. Internal so
    /// PoolLocatorGuardTests can cref it; the pin hardcodes 300 so a mutated constant
    /// cannot retune the test with it. See Display.Flight.cs for the test-accessor convention.</summary>
    internal const int ProgressLogEveryTicks = 300;   // 1.2GB/tick-count at LocateBudgetInBattle (4.8GB out of battle, LocateBudgetOutOfBattle); a readable cadence, not a measured one

    internal readonly record struct LocateCompletion(int Regions, int Ticks, long Bytes, long ElapsedMs, string Trigger);

    // Restart coalescing. Invalidate() queues a restart instead of clearing _cached and
    // never touches a running scan, so any number of Invalidate() calls mid-scan collapse into one
    // restart (the livelock fix, since battle edges fire several per battle). A failed revalidate
    // does not set _restartPending; it runs only with no scan in flight and falls through to
    // _scan.Begin in the same Step.
    private bool _restartPending;
    private bool _stale = true;   // true until the very first publish; RegionsStale's backing field
    private string _pendingTrigger = "first";
    // Snapshot of _pendingTrigger taken when the scan begins, so a mid-flight Invalidate() cannot
    // change the trigger reported for the run in flight.
    private string _runningTrigger = "first";
    private bool _hasScannedOnce;   // true once any scan has completed, found something or not -- distinguishes "first" from "empty-retry"

    private long _lastRevalidateMs = long.MinValue / 2;   // sentinel so the first check is always due (same as DisplaySweep.cs)
    internal int _revalidateCount;   // test-only. How many times the Step-driven revalidate cadence actually fired

    /// <summary>True from an <see cref="Invalidate"/> (or a failed periodic revalidate) until the
    /// next publish. Display.Heartbeat.cs's ProcessPending reads it, since a stale cache is as
    /// untrustworthy as an empty one.</summary>
    internal bool RegionsStale => _stale;

    /// <summary>Bumped on every <see cref="Publish"/> and <see cref="SeedForTest"/>
    /// (PoolLocator.cs), never reset (commit 1B). Display.PoolPaint.cs's MaybePoolPaint compares it
    /// with the generation it last scanned, so a fresh region list is scanned at once. An int
    /// rather than a bool because Publish mutates CachedRegions in place (Clear+AddRange), so
    /// reference identity cannot signal a change.</summary>
    internal int PublishGeneration { get; private set; }

    /// <summary>Advance the resumable scan by up to <paramref name="budgetBytes"/>, or run the
    /// cheap periodic revalidate when nothing else is due. Called every Engine tick on the
    /// "pool-locate" lane by Display.PoolLocate.cs's StepPoolLocate, and by Display.PoolPaint.cs's
    /// MaybePoolPaint. Publishes into <see cref="CachedRegions"/> only on the tick a scan
    /// completes and returns a non-null <see cref="LocateCompletion"/> on exactly that tick,
    /// otherwise null.</summary>
    internal LocateCompletion? Step(long budgetBytes, long nowMs)
    {
        // Runs once before the cold scan (see PoolLocator.WarmStart.cs; no-op without a sidecar).
        if (!_warmStartAttempted)
        {
            _warmStartAttempted = true;
            SeedFromSidecar();
        }

        if (!_scan.Running)
        {
            // The cadence gate covers both a populated and an empty cache. Without it, a scan that found
            // nothing (cold boot before the pool exists) restarted on the very next call at the full
            // per-tick budget. A queued restart (Invalidate() or a prior failed revalidate) skips the wait.
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
                    if (stillPool) return null;   // still good. Nothing to do this tick
                    _pendingTrigger = "revalidate";
                }
                else
                {
                    _pendingTrigger = _hasScannedOnce ? "empty-retry" : "first";
                }
            }
            // Round 3 (C5): not redundant with Publish's `_stale = _restartPending`. A revalidate or
            // empty-retry follows a Publish that cleared _stale, so without this line staleness would
            // read false while the new scan is in flight.
            _stale = true;
            _restartPending = false;
            _runningTrigger = _pendingTrigger;   // snapshot before a later Invalidate() can move _pendingTrigger
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

    /// <summary>Queue a restart instead of clearing the cache. A running scan is not
    /// aborted, and CachedRegions keeps serving the last list, marked <see cref="RegionsStale"/>,
    /// until the restart republishes. Call on battle exit or when the menu's render buffers are
    /// reallocated.</summary>
    internal void Invalidate()
    {
        _restartPending = true;
        _stale = true;
        _pendingTrigger = "invalidate";
    }

    /// <summary>Writes a fresh scan result into the cache. It lives here beside _stale and
    /// Invalidate because it decides when staleness may clear; PoolLocator.cs keeps CachedRegions
    /// and SeedForTest.</summary>
    private void Publish(IReadOnlyList<(long baseAddr, long size)> regions, long nowMs)
    {
        _cached.Clear();
        _cached.AddRange(regions);
        // B1. Staleness must survive a publish while a restart is still queued. A scan running when
        // Invalidate() lands straddles the relocation and publishes on its own, so its result is the
        // one Invalidate() distrusted. Step clears _restartPending when it picks the restart up, so
        // the queued scan's own publish clears staleness correctly.
        _stale = _restartPending;
        _hasScannedOnce = true;
        // Stamp the revalidate clock at publish, not at scan start. A scan can outlast RevalidateMs
        // (the live tape saw 52 to 145MB regions), and a second Step in the finishing tick would
        // otherwise run an unbudgeted AllCachedStillPool right away. MaybePoolPaint's fall-through
        // call was removed in R1, but the guard is still tested.
        _lastRevalidateMs = nowMs;
        PublishGeneration++;   // commit 1B. Every publish is a "fresh region list" event
        LogRegionsFound();
        MaybeSaveSidecar();   // dirty-checked write-on-completion, PoolLocator.WarmStart.cs
    }
}
