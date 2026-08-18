using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Retune round (R1+R3): production now drives the resumable pool locate from exactly ONE call
/// site (Engine's own "pool-locate" phase). Display.PoolPaint.cs's MaybePoolPaint used to ALSO
/// call StepPoolLocate on its own fall-through (LW-261 verify-round FIX 5, this file's own
/// original test); the owner's live tape showed that costing the scan's ~45ms region snapshot
/// roughly twice a tick (PoolScan.cs's SnapshotRefreshMs doc has the arithmetic), so this file now
/// pins the opposite contract: a bare Display.Tick() call drives ZERO locate progress on its own,
/// and the ONE surviving call site picks the correct battle-aware budget.
/// </summary>
public class DisplayPoolLocateBudgetTests
{
    private static (Display display, List<(string type, string payload)> recorded) BuildFixture()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        // Small enough to complete in a single Step call: if anything besides the one intended
        // call site drove the locate, a "locate-complete" record would appear immediately.
        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        long poolBase = 0x76_0000_0000L;
        long staticsBase = 0x77_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true,
            recorder: (t, p) => recorded.Add((t, p)));
        return (display, recorded);
    }

    /// <summary>R1's own contract: a bare Display.Tick() call, with StepPoolLocate never called
    /// anywhere, must drive zero locate progress -- pinned via the "locate-complete" flight
    /// record, the one observable side effect a real PoolLocator.Step completion produces.</summary>
    [Fact]
    public void Display_Tick_alone_drives_zero_locate_completions()
    {
        var (display, recorded) = BuildFixture();

        display.Tick(false);

        Assert.DoesNotContain(recorded, r => r.type == "card" && r.payload.StartsWith("locate-complete"));
    }

    /// <summary>The other half of "exactly one driver": StepPoolLocate itself, called directly
    /// (Engine's own call site, in production), still completes the same fixture.</summary>
    [Fact]
    public void StepPoolLocate_called_directly_still_completes_the_locate()
    {
        var (display, recorded) = BuildFixture();

        display.StepPoolLocate(false);

        Assert.Contains(recorded, r => r.type == "card" && r.payload.StartsWith("locate-complete"));
    }

    /// <summary>R3: the in-battle budget reads exactly one chunk per Step call; the out-of-battle
    /// budget reads four. Asserted via CHUNK COUNT (BytesSpyMem.ChunkReads), not bytes -- the
    /// out-of-battle budget no longer equals ChunkSize, so a byte-only bound could not tell "one
    /// big chunk" from "four small ones" the way a chunk count can.</summary>
    [Fact]
    public void StepPoolLocate_selects_the_battle_aware_budget_by_chunk_count()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        // Larger than even the out-of-battle budget (16MB), no pool shape: neither call below
        // completes in one Step, so each spends its own full budget, not a short-circuit.
        var filler = new byte[24 * 1024 * 1024];
        long fillerBase = 0x78_0000_0000L;
        long staticsBase = 0x79_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;

        var inBattleHeap = new FakeHeap((fillerBase, filler, true), (staticsBase, statics, true));
        var inBattleSpy = new BytesSpyMem(inBattleHeap);
        var inBattleDisplay = new Display(meta, kills,
            new OffsetRemapMem(inBattleSpy, staticsBase, staticsBase + 2, staticsBase + 4),
            clock.Func, legends: null, poolPaint: true);
        inBattleDisplay.StepPoolLocate(true);
        Assert.Equal(1, inBattleSpy.ChunkReads);

        var outOfBattleHeap = new FakeHeap((fillerBase, filler, true), (staticsBase, statics, true));
        var outOfBattleSpy = new BytesSpyMem(outOfBattleHeap);
        var outOfBattleDisplay = new Display(meta, kills,
            new OffsetRemapMem(outOfBattleSpy, staticsBase, staticsBase + 2, staticsBase + 4),
            clock.Func, legends: null, poolPaint: true);
        outOfBattleDisplay.StepPoolLocate(false);
        Assert.Equal(4, outOfBattleSpy.ChunkReads);
    }
}
