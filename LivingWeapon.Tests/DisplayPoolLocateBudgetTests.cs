using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-261 verify-round FIX 5: PoolScanTests bounds a SINGLE Step call's own byte spend; nothing
/// bounded the TOTAL across a single real Engine tick, where Display.PoolPaint.cs's MaybePoolPaint
/// and Engine's own "pool-locate" phase both call Display.StepPoolLocate -- a documented, accepted
/// deviation (Display.PoolPaint.cs's own class doc), but its real cost (roughly twice
/// PoolLocator.LocateBudgetBytes, not once) had no test pinning the number.
/// </summary>
public class DisplayPoolLocateBudgetTests
{
    [Fact]
    public void Two_same_tick_call_sites_never_exceed_roughly_twice_the_locate_budget()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        // No pool-shaped bytes anywhere and larger than one budget slice: the scan never
        // completes in one Step (so each call below spends its own full budget, not a
        // short-circuit) -- the fixture only needs to outlast two Step calls, not model coverage.
        var filler = new byte[24 * 1024 * 1024];   // larger than the 2x combined bound (16MB) so an unbudgeted single-call read would visibly exceed it
        long fillerBase = 0x72_0000_0000L;
        long staticsBase = 0x73_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((fillerBase, filler, true), (staticsBase, statics, true));
        var spy = new BytesSpyMem(heap);
        var wrapped = new OffsetRemapMem(spy, staticsBase, staticsBase + 2, staticsBase + 4);
        var display = new Display(meta, kills, wrapped, clock.Func, legends: null, poolPaint: true);

        long before = spy.TotalBytesRead;

        // ONE real Engine tick's worth of calls, driven directly rather than through Tick()/
        // MaybePoolPaint: production has TWO call sites that can each reach StepPoolLocate on the
        // same real tick (Engine's own "pool-locate" phase, and Display.PoolPaint.cs's
        // MaybePoolPaint on its own fall-through -- that file's class doc explains why both call
        // it), and this fixture stands in for both by calling the shared method twice, the exact
        // same-tick race that doc accepts.
        display.StepPoolLocate();
        display.StepPoolLocate();

        long spentThisTick = spy.TotalBytesRead - before;
        long perCallBound = PoolLocator.LocateBudgetBytes + DisplaySweep.ChunkSize;   // PoolScanTests' own per-Step bound
        long bound = 2 * perCallBound;

        Assert.True(spentThisTick <= bound,
            $"two same-tick call sites read {spentThisTick} bytes, expected at most {bound} (2x the per-call bound)");
        // Non-vacuity: both calls must have actually done real budgeted work, not short-circuited
        // (a cadence bug or a coverage latch would make this trivially small).
        Assert.True(spentThisTick > PoolLocator.LocateBudgetBytes,
            $"fixture sanity: both calls must have spent real budget, only read {spentThisTick} bytes");
    }
}
