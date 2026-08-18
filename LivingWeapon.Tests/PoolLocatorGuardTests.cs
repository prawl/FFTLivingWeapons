using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-267: three guards inside the resumable pool search (PoolLocator.Restart.cs, PoolScan.cs)
/// survived deliberate mutation with the whole suite green -- filed as unpinned findings, not
/// bugs (docs/TODO.md's own framing: "None is believed wrong today; they are simply unpinned").
/// This file pins the two PoolLocator.Restart.cs guards -- the unconditional `_stale = true;` in
/// Step, and the ProgressLogEveryTicks progress-log cadence; PoolScanTests.cs pins the third
/// (PoolScan's read==0 skip-not-abandon branch) alongside its own existing PoolScan coverage.
/// Fixture idioms (BuildMeta/BuildPoolBuffer, FakeHeap, PoolLocator driven directly) mirror
/// PoolLocatorStepTests.cs's own.
/// </summary>
public class PoolLocatorGuardTests
{
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 1, new WeaponMeta { Name = "Sword", Flavor = "A sharp blade" } },
    };

    private static byte[] BuildPoolBuffer()
    {
        var buf = new byte[500];
        CardFixtures.WriteCardForwardWithName(buf, 0, "Sword", "A sharp blade");
        return buf;
    }

    /// <summary>PIN (LW-267): Step's unconditional `_stale = true;` (PoolLocator.Restart.cs, just
    /// above `_restartPending = false;`) -- its own comment explains it covers two of the three
    /// paths into a fresh scan (a revalidate that just found the cache stale, or an
    /// empty-retry/first attempt), both of which follow a PRIOR Publish that cleared _stale to
    /// false, so without this line staleness would read false while a brand-new scan is still in
    /// flight. This pins the revalidate path specifically -- the dangerous one, a caller reading
    /// RegionsStale mid-rescan and trusting the just-invalidated published set. A large filler
    /// region (24MB, no pool bytes) keeps the revalidate-triggered rescan from completing inside
    /// the same Step call that starts it, so there is a real window to observe RegionsStale while
    /// genuinely in flight -- confirmed non-vacuous by driving one more round of Steps to an
    /// actual completion afterward, so the in-flight assert cannot be passing because the scan had
    /// secretly already finished and republished.
    ///
    /// RED under MUTATION (delete the guarded line): the first scan's own Publish already cleared
    /// _stale to false (that Publish ran with _restartPending false, so `_stale = _restartPending`
    /// left it false); with the guard line gone, nothing marks the flag again before the fresh
    /// scan actually completes, so RegionsStale reads false throughout the in-flight rescan and
    /// the in-flight assert below fails.</summary>
    [Fact]
    public void RegionsStale_reads_true_while_a_revalidate_triggered_rescan_is_in_flight()
    {
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x50_0000_0000L;
        long fillerBase = 0x51_0000_0000L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.NotNull(first);   // tiny fixture: the cold scan completes in one call
        Assert.False(locator.RegionsStale, "a completed scan must publish as non-stale");

        // The published region is gone (relocated/realloc'd away) and a filler far larger than
        // one Step's budget takes its place, so the revalidate-triggered rescan this drives
        // cannot complete inside the same Step call that starts it.
        heap.RemoveRegion(poolBase);
        heap.AddRegion(fillerBase, new byte[24 * 1024 * 1024], writable: true);

        now += PoolLocator.RevalidateMs + 1;
        var second = locator.Step(PoolLocator.LocateBudgetInBattle, now);

        Assert.Null(second);   // still in flight -- not a completed-and-republished pass
        Assert.True(locator.RegionsStale,
            "staleness must read true while the revalidate-triggered rescan is still in flight");

        // Non-vacuity: drive the SAME in-flight scan to a real completion afterward, proving the
        // window above genuinely needed more than one Step (never secretly already done).
        PoolLocator.LocateCompletion? completion = null;
        for (int i = 0; i < 20 && completion == null; i++)
        {
            now += 33;
            completion = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }
        Assert.NotNull(completion);
    }

    /// <summary>PIN (LW-267): ProgressLogEveryTicks (PoolLocator.Restart.cs) gates the "still
    /// scanning" progress line to fire only once every 300 Steps of an in-flight scan. A 1-byte
    /// budget against many tiny (16-byte) regions makes each Step consume exactly one region --
    /// verified empirically for this fixture shape: the first successful read of a Step call
    /// always proceeds regardless of remaining budget (PoolScan.Step's own forward-progress
    /// guarantee), a 16-byte region is fully covered by that single read, and every later chunk
    /// read attempted in the SAME call is budget-gated -- so with a 1-byte budget, tick count and
    /// Step call count land identical, without needing gigabytes of fixture data to reach tick
    /// 300.
    ///
    /// The target tick count (300) is a LITERAL here, deliberately not
    /// <see cref="PoolLocator.ProgressLogEveryTicks"/> itself: an earlier draft of this test drove
    /// the loop bound and the expected log fragment off that same constant, which made it adapt to
    /// whatever value the constant held rather than pin the tuned one -- MUTATION R2 (300 -> 3000)
    /// passed it clean (the loop just ran 2999 times and asked for "...after 3000 ticks", which
    /// still never logs because the scan is still shy of that new, larger cadence, so the negative
    /// assert alone can't distinguish "cadence not yet due" from "cadence guard deleted"). Pinning
    /// the literal makes the test ask a real question -- "does a line naming tick 300 exist" --
    /// that MUTATION R2 genuinely cannot answer yes to.
    ///
    /// RED under MUTATION R2 (300 -> 3000): no progress line has appeared by the time this test's
    /// 300 Steps are spent (3000 %% 300 dropped, the mutated modulo needs 3000 ticks now), so the
    /// line-naming-300 assert fails.</summary>
    [Fact]
    public void Progress_logs_once_at_the_configured_tick_cadence_not_before()
    {
        var pats = new CardPatterns(BuildMeta());
        const int targetTick = 300;   // literal on purpose -- see the doc comment above
        long baseAddr = 0x52_0000_0000L;
        int totalRegions = targetTick + 10;   // still running past tick 300
        var regions = new (long baseAddr, byte[] data, bool writable)[totalRegions];
        for (int i = 0; i < totalRegions; i++)
        {
            regions[i] = (baseAddr, new byte[16], true);
            baseAddr += 0x1_0000;   // distinct, well-separated, non-overlapping bases
        }
        var heap = new FakeHeap(regions);
        var locator = new PoolLocator(heap, pats);

        using var cap = LogCapture.Start();
        long now = 0;
        for (int i = 0; i < targetTick - 1; i++)
        {
            var completion = locator.Step(1, now);
            now += 33;
            Assert.Null(completion);   // fixture sanity: still running, nowhere near done
        }
        Assert.DoesNotContain(cap.File, l => l.Contains("still scanning after"));

        var last = locator.Step(1, now);
        Assert.Null(last);   // fixture sanity: 300 of 310 regions consumed, still running

        Assert.Contains(cap.File, l => l.Contains($"still scanning after {targetTick} ticks"));
    }
}
