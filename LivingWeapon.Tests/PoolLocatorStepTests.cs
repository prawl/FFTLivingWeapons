using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-261: PoolLocator.Step -- the production entry point that drives the resumable PoolScan and
/// publishes into CachedRegions. Distinct from PoolScanTests (the walk's own budget/termination
/// contract) and from PoolLocatorTests (the pre-existing cache-or-rescan behavior, still exercised
/// through LocateAll). Fixture idioms (WriteCardForwardWithName, BuildPoolBuffer-shaped buffers)
/// mirror PoolLocatorTests.cs's own.
/// </summary>
public class PoolLocatorStepTests
{
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 1, new WeaponMeta { Name = "Sword", Flavor = "A sharp blade" } },
        { 2, new WeaponMeta { Name = "Staff", Flavor = "B holy relic" } },
    };

    private static byte[] BuildPoolBuffer()
    {
        var buf = new byte[500];
        CardFixtures.WriteCardForwardWithName(buf, 0, "Sword", "A sharp blade");
        return buf;
    }

    /// <summary>CachedRegions must stay unchanged (still empty, the initial state) on every
    /// intermediate Step while a scan is in flight, and change EXACTLY ONCE -- on the tick the
    /// scan completes. The real pool region sits at the LOWER address (scanned and internally
    /// staged FIRST) and a filler region larger than the budget sits above it, forcing several
    /// more Step calls before the whole scan actually finishes -- so there is a real window where
    /// PoolScan's own internal staging already holds the pool region while the scan is still
    /// in-flight, which is exactly the condition that would expose a premature publish.</summary>
    [Fact]
    public void Regions_are_never_published_before_completion()
    {
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x20_0000_0000L;
        long fillerBase = 0x21_0000_0000L;
        var filler = new byte[12 * 1024 * 1024];   // > LocateBudgetInBattle: forces several more Step calls
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true), (fillerBase, filler, true));
        var locator = new PoolLocator(heap, pats);

        int publishCount = 0;
        int lastCount = locator.CachedRegions.Count;
        Assert.Equal(0, lastCount);

        long now = 0;
        PoolLocator.LocateCompletion? completion = null;
        for (int i = 0; i < 20 && completion == null; i++)
        {
            completion = locator.Step(PoolLocator.LocateBudgetInBattle, now);
            now += 33;
            int cur = locator.CachedRegions.Count;
            if (cur != lastCount)
            {
                publishCount++;
                lastCount = cur;
                // The one moment CachedRegions is allowed to change must coincide with THIS SAME
                // Step call reporting completion -- not an internal partial stage (PoolScan's own
                // staging list can legitimately hold the pool region long before the whole
                // multi-region walk finishes; only a real Complete=true may ever surface it).
                Assert.NotNull(completion);
            }
        }

        Assert.NotNull(completion);
        Assert.Equal(1, publishCount);
        Assert.Single(locator.CachedRegions);
        Assert.Equal(poolBase, locator.CachedRegions[0].baseAddr);
    }

    /// <summary>The Measurement-1 hazard, pinned: a region committed to the heap AFTER the scan
    /// already started must still be found, because a per-slice re-snapshot (not a frozen region
    /// list taken once at Begin) drives the walk.</summary>
    [Fact]
    public void Scan_sees_a_region_committed_after_the_scan_started()
    {
        var pats = new CardPatterns(BuildMeta());
        var filler = new byte[12 * 1024 * 1024];
        long fillerBase = 0x30_0000_0000L;
        var heap = new FakeHeap((fillerBase, filler, true));
        var locator = new PoolLocator(heap, pats);

        long now = 0;
        var r1 = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.Null(r1);   // the filler alone cannot complete in one 4MB-budget Step

        // A region committed AFTER the scan started, addressed higher than the filler so the
        // still-in-progress cursor reaches it on a later Step.
        long poolBase = 0x31_0000_0000L;
        heap.AddRegion(poolBase, BuildPoolBuffer(), writable: true);

        PoolLocator.LocateCompletion? completion = null;
        for (int i = 0; i < 20 && completion == null; i++)
        {
            // Retune round (R2): past PoolScan.SnapshotRefreshMs every call, not just 33ms --
            // otherwise the cadence gate reuses the stale pre-commit snapshot the whole way
            // through and this hazard is invisible by construction.
            now += PoolScan.SnapshotRefreshMs + 1;
            completion = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }

        Assert.NotNull(completion);
        Assert.Contains(locator.CachedRegions, r => r.baseAddr == poolBase);
    }

    /// <summary>The livelock guard: Invalidate() firing on EVERY tick must never abort or reset
    /// the scan already in flight -- otherwise a scan could never finish under this mod's own
    /// battle-edge load (several Invalidate() calls per battle).</summary>
    [Fact]
    public void Continuous_invalidates_still_let_a_scan_complete()
    {
        var pats = new CardPatterns(BuildMeta());
        var filler = new byte[12 * 1024 * 1024];
        long fillerBase = 0x40_0000_0000L;
        long poolBase = 0x41_0000_0000L;
        var heap = new FakeHeap((fillerBase, filler, true), (poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        long now = 0;
        PoolLocator.LocateCompletion? completion = null;
        for (int i = 0; i < 20 && completion == null; i++)
        {
            locator.Invalidate();   // fires every tick -- must not abort the running scan
            completion = locator.Step(PoolLocator.LocateBudgetInBattle, now);
            now += 33;
        }

        Assert.NotNull(completion);
        Assert.Contains(locator.CachedRegions, r => r.baseAddr == poolBase);
    }

    /// <summary>FIX 6 (adversarial verify round; round 5 verify, F8: the "second call site" this
    /// doc originally described -- MaybePoolPaint's own fall-through, in addition to Engine's
    /// pool-locate phase -- was REMOVED by this arc's own later R1 retune, so this is now past
    /// tense as a PRODUCTION scenario; the guard itself is still correct and still tested directly
    /// here, since any future caller could still Step twice in the same tick). A caller stepping
    /// Step again in the SAME real tick a scan just completed used to be exactly this bug: a
    /// multi-tick scan (filler region forces several Step calls) is the real shape it bit --
    /// _lastRevalidateMs was only ever stamped inside the cadence-gated revalidate branch OR at
    /// the moment a NEW scan starts -- never at the moment one actually FINISHES, several ticks
    /// later -- so a second Step call landing on the exact tick the scan published saw a timestamp
    /// several ticks stale, read the cadence as "overdue", and ran a full unbudgeted
    /// AllCachedStillPool over every just-published region on the spot. Pinned via
    /// _revalidateCount staying at 0 across the completing call and the same-tick second call
    /// right after it.</summary>
    [Fact]
    public void Revalidate_does_not_run_in_the_same_tick_a_scan_just_published()
    {
        var pats = new CardPatterns(BuildMeta());
        // Sized (with the 33ms/tick, LocateBudgetInBattle/tick production cadence) so the scan's
        // OWN wall-clock duration exceeds RevalidateMs by the time it completes -- 160MB / 4MB
        // per tick = 40 ticks, 40 * 33ms ~= 1320ms > RevalidateMs (1000ms). The live tape this
        // arc measured saw 52 to 145MB of real pool regions, so this is not a contrived size.
        var filler = new byte[160 * 1024 * 1024];
        long fillerBase = 0x60_0000_0000L;
        long poolBase = 0x61_0000_0000L;
        var heap = new FakeHeap((fillerBase, filler, true), (poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        long now = 0;
        PoolLocator.LocateCompletion? completion = null;
        for (int i = 0; i < 60 && completion == null; i++)
        {
            completion = locator.Step(PoolLocator.LocateBudgetInBattle, now);
            now += 33;
        }
        Assert.NotNull(completion);
        Assert.True(now - 33 > PoolLocator.RevalidateMs,
            $"fixture sanity: the scan must take longer than RevalidateMs to complete (took {now - 33}ms)");
        Assert.Equal(0, locator._revalidateCount);

        // A caller stepping Step again the SAME tick (same nowMs) right after the publish.
        var second = locator.Step(PoolLocator.LocateBudgetInBattle, now - 33);

        Assert.Null(second);
        Assert.Equal(0, locator._revalidateCount);
    }

    /// <summary>ROUND 2 VERIFY, B1 (blocker): a scan already in flight when Invalidate() lands
    /// straddles the relocation -- its own Publish() must not clear RegionsStale, because the
    /// restart Invalidate() queued has not run yet. RED against the pre-fix code: Step's "not
    /// running" branch only sets _stale=true inside `if (!_restartPending)`, so the tick that
    /// picks up the queued restart (where _restartPending is true, skipping that block entirely)
    /// never re-marks the flag, leaving it false (cleared by the straddled scan's own Publish)
    /// for the queued restart's whole run.</summary>
    [Fact]
    public void RegionsStale_stays_true_across_a_scan_that_straddled_an_invalidate_until_the_queued_restart_publishes()
    {
        var pats = new CardPatterns(BuildMeta());
        var filler = new byte[12 * 1024 * 1024];   // > LocateBudgetInBattle: forces several Step calls
        long fillerBase = 0x80_0000_0000L;
        long poolBase = 0x81_0000_0000L;
        var heap = new FakeHeap((fillerBase, filler, true), (poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.Null(first);   // still in flight

        locator.Invalidate();   // lands MID-FLIGHT: the running scan straddles this
        Assert.True(locator.RegionsStale);

        PoolLocator.LocateCompletion? straddled = null;
        for (int i = 0; i < 20 && straddled == null; i++)
        {
            now += 33;
            straddled = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }
        Assert.NotNull(straddled);
        Assert.True(locator.RegionsStale,
            "a scan that straddled a mid-flight Invalidate must not clear RegionsStale -- the queued restart has not published yet");

        PoolLocator.LocateCompletion? restart = null;
        for (int i = 0; i < 20 && restart == null; i++)
        {
            now += 33;
            restart = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }
        Assert.NotNull(restart);
        Assert.False(locator.RegionsStale);
    }

    /// <summary>ROUND 2 VERIFY, B2 (blocker): _pendingTrigger is a single field Invalidate()
    /// overwrites mid-flight, and the completion record reported whatever it held at PUBLISH
    /// time, not at the time the reporting scan actually STARTED. RED against the pre-fix code:
    /// a scan that began with an empty cache (trigger "first") gets invalidated while still
    /// running, so by the time it finishes _pendingTrigger already reads "invalidate", and the
    /// straddled scan's own completion wrongly reports trigger=invalidate instead of the "first"
    /// it actually was.</summary>
    [Fact]
    public void Completion_trigger_reflects_when_the_scan_started_not_a_mid_flight_invalidate()
    {
        var pats = new CardPatterns(BuildMeta());
        var filler = new byte[12 * 1024 * 1024];
        long fillerBase = 0x82_0000_0000L;
        long poolBase = 0x83_0000_0000L;
        var heap = new FakeHeap((fillerBase, filler, true), (poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.Null(first);

        locator.Invalidate();   // mid-flight

        PoolLocator.LocateCompletion? straddled = null;
        for (int i = 0; i < 20 && straddled == null; i++)
        {
            now += 33;
            straddled = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }
        Assert.NotNull(straddled);
        Assert.Equal("first", straddled!.Value.Trigger);

        PoolLocator.LocateCompletion? restart = null;
        for (int i = 0; i < 20 && restart == null; i++)
        {
            now += 33;
            restart = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }
        Assert.NotNull(restart);
        Assert.Equal("invalidate", restart!.Value.Trigger);
    }

    /// <summary>ROUND 2 VERIFY, B3: a mutation that moves `_restartPending = false` INSIDE the
    /// `if (!_restartPending)` block leaves the flag permanently true after one Invalidate, so
    /// every later tick bypasses the RevalidateMs cadence and restarts a full scan forever --
    /// the same bug class as round 1's empty-cache finding. This pins the settle-back-down
    /// behavior directly: one Invalidate, then several ticks well within RevalidateMs of the
    /// restart's own publish, must NOT trigger another scan each time.</summary>
    [Fact]
    public void After_one_invalidate_the_locator_settles_back_into_normal_cadence_not_perpetual_rescanning()
    {
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x9500L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var spy = new RegionsSpyMem(heap);
        var locator = new PoolLocator(spy, pats);

        long now = 0;
        var seed = locator.Step(PoolLocator.LocateBudgetInBattle, now);   // establishes the cache
        Assert.NotNull(seed);

        locator.Invalidate();
        now += 33;
        var restart = locator.Step(PoolLocator.LocateBudgetInBattle, now);   // picks up the queued restart, completes (tiny fixture)
        Assert.NotNull(restart);

        int callsAfterRestart = spy.RegionsCalls;

        for (int i = 0; i < 5; i++)
        {
            now += 33;
            var r = locator.Step(PoolLocator.LocateBudgetInBattle, now);
            Assert.Null(r);
        }
        Assert.Equal(callsAfterRestart, spy.RegionsCalls);
    }

    /// <summary>FIX 1 (adversarial verify round): the empty-cache path had NO cadence at all --
    /// a scan that finds nothing restarted on the very next Step call, forever, at
    /// PoolLocator.LocateBudgetInBattle per tick on the Always lane. RED against the pre-fix code:
    /// the fixture has no pool-shaped bytes anywhere, so the first Step completes with
    /// regions=0, and the second Step (33ms later, well under RevalidateMs) must NOT have started
    /// a second scan -- pinned by the Regions() call count staying flat, since PoolScan.Step's own
    /// Snapshot() is the one place Regions() is ever invoked.</summary>
    [Fact]
    public void Empty_result_does_not_restart_a_new_scan_before_the_revalidate_cadence_elapses()
    {
        var pats = new CardPatterns(BuildMeta());
        var heap = new FakeHeap((0x5000L, new byte[64], true));   // no pool-shaped content anywhere
        var spy = new RegionsSpyMem(heap);
        var locator = new PoolLocator(spy, pats);

        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.NotNull(first);
        Assert.Equal(0, first!.Value.Regions);
        int callsAfterFirstScan = spy.RegionsCalls;
        Assert.True(callsAfterFirstScan > 0, "the first (cold) scan must have walked Regions()");

        now += 33;   // well under RevalidateMs
        var second = locator.Step(PoolLocator.LocateBudgetInBattle, now);

        Assert.Null(second);
        Assert.Equal(callsAfterFirstScan, spy.RegionsCalls);
    }

    /// <summary>AllCachedStillPool's periodic reverify must fire at most once per
    /// PoolLocator.RevalidateMs, not on every Step call.</summary>
    [Fact]
    public void Revalidation_runs_at_most_once_per_cadence()
    {
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x9000L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        // Seed through Step itself (not LocateAll, whose own clock is Environment.TickCount64,
        // not this test's simulated `now`) so FIX 6's publish-time stamp and every check below
        // share one consistent timeline.
        long now = 0;
        var seed = locator.Step(PoolLocator.LocateBudgetInBattle, now);   // cold: completes in one call (tiny fixture)
        Assert.NotNull(seed);
        Assert.Equal(0, locator._revalidateCount);   // FIX 6: publish itself must not count as a revalidate

        for (int i = 0; i < 5; i++)
        {
            now += 100;
            locator.Step(PoolLocator.LocateBudgetInBattle, now);   // all well within RevalidateMs of the seed publish
        }
        Assert.Equal(0, locator._revalidateCount);

        now += PoolLocator.RevalidateMs + 1;
        locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.Equal(1, locator._revalidateCount);

        for (int i = 0; i < 5; i++)
        {
            now += 100;
            locator.Step(PoolLocator.LocateBudgetInBattle, now);   // all well within RevalidateMs of the last revalidate
        }
        Assert.Equal(1, locator._revalidateCount);

        now += PoolLocator.RevalidateMs + 1;
        locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.Equal(2, locator._revalidateCount);
    }

    /// <summary>IGameMemory wrapper that counts Regions() calls, forwarding everything else --
    /// copied from PoolLocatorTests.cs's private helper of the same name (this file's own
    /// no-cross-file-reference convention for test-only fakes).</summary>
    private sealed class RegionsSpyMem : IGameMemory
    {
        private readonly IGameMemory _inner;
        public int RegionsCalls { get; private set; }
        public RegionsSpyMem(IGameMemory inner) => _inner = inner;

        public byte U8(long addr) => _inner.U8(addr);
        public ushort U16(long addr) => _inner.U16(addr);
        public bool TryReadBytes(long addr, int len, out byte[] buf) => _inner.TryReadBytes(addr, len, out buf);
        public int ReadInto(long addr, byte[] buf, int len) => _inner.ReadInto(addr, buf, len);
        public void WriteBytes(long addr, byte[] data) => _inner.WriteBytes(addr, data);
        public void W8(long addr, byte value) => _inner.W8(addr, value);
        public bool Readable(long addr, int len) => _inner.Readable(addr, len);
        public bool Writable(long addr, int len) => _inner.Writable(addr, len);
        public IEnumerable<(long baseAddr, long size)> Regions()
        {
            RegionsCalls++;
            return _inner.Regions();
        }
    }
}
