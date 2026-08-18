using System.Collections.Generic;
using System.Linq;
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

    /// <summary>LW-266: the locate-complete flight tap (Display.Flight.cs's
    /// RecordLocateCompleteIfTapped) is the record the owner reads a live pass's completion
    /// numbers off, and two PRIOR mutations to it (swapping which counter it checks, and which
    /// counter it spends) both left this suite green -- nothing exercised its own budget/reset
    /// contract directly. This pins both halves: the tap clamps at its OWN reserve
    /// (<see cref="Display.LocateRecordBudget"/>), separate from every other flight lane's
    /// budget, and <see cref="Display.Invalidate"/> resets that reserve for the next window
    /// (Display.cs's own reset trio -- _flightBudget/_coverageBudget/_locateFlightBudget all
    /// zeroed together).
    ///
    /// The heap carries NO pool-shaped bytes anywhere (a statics region only), so every scan
    /// completes having found nothing: PoolLocator.CachedRegions never fills, which keeps every
    /// completion after the first on the empty-retry lane (PoolLocator.Restart.cs's own
    /// "_hasScannedOnce ? empty-retry : first" branch) rather than the revalidate one -- the
    /// cheapest way to drive many completions from one tiny fixture.</summary>
    [Fact]
    public void Locate_complete_records_stop_at_their_own_budget_and_reset_per_window()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();
        clock.Ms = 100_000;   // repo hygiene: nonzero origin, not load-bearing

        long staticsBase = 0x7A_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10;
        var heap = new FakeHeap((staticsBase, statics, true));

        var recorded = new List<(string type, string payload)>();
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock,
            poolPaint: true, recorder: (t, p) => recorded.Add((t, p)));

        // Drive completions through the empty-retry lane: each iteration's scan finds nothing
        // and completes within that single call (the heap is tiny), emitting one locate-complete
        // tap attempt every time. LocateRecordBudget + 5 attempts, well past the clamp.
        for (int i = 0; i < Display.LocateRecordBudget + 5; i++)
        {
            clock.Ms += PoolLocator.RevalidateMs + 1;
            display.StepPoolLocate(false);
        }

        // Premise assert: all 13 completions genuinely happened (each Publish bumps
        // PublishGeneration), so the 8 below is the record budget clamping, not completions
        // drying up (LW-266 verify round).
        Assert.Equal(Display.LocateRecordBudget + 5, display._poolLocator.PublishGeneration);

        var completes = recorded.Where(r => r.type == "card" && r.payload.StartsWith("locate-complete")).ToList();
        Assert.Equal(Display.LocateRecordBudget, completes.Count);
        Assert.Contains("trigger=first", completes[0].payload);
        Assert.Contains(completes, r => r.payload.Contains("trigger=empty-retry"));

        // Window reset: Invalidate() zeroes the locate tap's own reserve (Display.cs's reset
        // trio), so the very next completion after a fresh RevalidateMs wait is recorded again
        // instead of staying clamped at the old window's cap.
        display.Invalidate();
        clock.Ms += PoolLocator.RevalidateMs + 1;
        display.StepPoolLocate(false);

        // Same premise, post-reset: one more genuine completion landed (LocateRecordBudget + 6),
        // not a completions-stopped false pass on the reset half of this test.
        Assert.Equal(Display.LocateRecordBudget + 6, display._poolLocator.PublishGeneration);

        var afterReset = recorded.Count(r => r.type == "card" && r.payload.StartsWith("locate-complete"));
        Assert.Equal(Display.LocateRecordBudget + 1, afterReset);
    }
}
