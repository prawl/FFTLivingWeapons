using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Display's pool-anchored in-place Kills paint (LW-37): once a writable pool region is
/// located and fully covers every tracked weapon id, the per-paint whole-heap DisplaySweep
/// is skipped. poolPaint is INJECTED (Display's ctor) so these tests reach the ON branch
/// under the PROD compile (LWDEV undefined), where Tuning.PoolPaintEnabled is always false.
/// Every test proves non-vacuity the same way: display._sweep.IsComplete stays FALSE when
/// the pool path is exercised (the sweep is literally never Tick()'d), so a correct paint
/// result can only have come from the pool path, never a fallback sweep pass.
/// </summary>
public class DisplayPoolPaintTests
{
    private readonly record struct PoolFixture(FakeHeap Heap, long StaticsBase, long PoolBase,
        Dictionary<int, WeaponMeta> Meta, Dictionary<int, int> Kills,
        int SlotA, int SuffixA, int SlotB, int SuffixB);

    /// <summary>Two weapons (ids 10, 11) packed name -> suffix slot -> Kills -> flavor, the
    /// realistic pool geometry, contiguous in one small (single-chunk) region. Mirror target
    /// is id 10.</summary>
    private static PoolFixture BuildTwoWeaponPoolFixture()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
            { 11, new WeaponMeta { Name = "BowY", Flavor = "Arrow never sleeps" } },
        };
        var kills = new Dictionary<int, int> { { 10, 0 }, { 11, 0 } };

        var poolBuf = new byte[2000];
        var (suffixA, slotA, flavorA) = CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        int nextStart = flavorA + ByteScan.Ascii("Fletched with regret").Length + 20;
        var (suffixB, slotB, _) = CardFixtures.WriteCardForwardWithName(poolBuf, nextStart, "BowY", "Arrow never sleeps");

        long poolBase = 0x50_0000_0000L;
        long staticsBase = 0x51_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;   // MirrorWeapon = 10

        var heap = new FakeHeap((poolBase, poolBuf, true), (staticsBase, statics, true));
        return new PoolFixture(heap, staticsBase, poolBase, meta, kills, slotA, suffixA, slotB, suffixB);
    }

    private static string ReadAscii(FakeHeap heap, long addr, int len)
    {
        heap.TryReadBytes(addr, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    // ─── forward multi-weapon attribution round trip (novel geometry) ─────────────

    [Fact]
    public void PoolPaint_registers_and_paints_the_correct_owner_for_each_weapon()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 7; f.Kills[11] = 3;
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        display.Tick(false);

        Assert.False(display._sweep.IsComplete);   // proves attribution came from the pool path

        Assert.Equal(Signatures.KillsMeterSlot(7), ReadAscii(f.Heap, f.PoolBase + f.SlotA, Signatures.KillsMeterSlotChars));
        Assert.Equal(Signatures.KillsMeterSlot(3), ReadAscii(f.Heap, f.PoolBase + f.SlotB, Signatures.KillsMeterSlotChars));
    }

    [Fact]
    public void PoolPaint_registers_and_paints_the_mirrored_weapons_suffix_too()
    {
        var f = BuildTwoWeaponPoolFixture();
        f.Kills[10] = 30;   // prod thresholds {5,25,50}: tier 2 -> "+2"
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        clock.Ms += DisplaySweep.HotRescanMs + 1;
        display.Tick(false);

        Assert.False(display._sweep.IsComplete);

        Assert.Equal(Signatures.KillsMeterSlot(30), ReadAscii(f.Heap, f.PoolBase + f.SlotA, Signatures.KillsMeterSlotChars));
        Assert.Equal("+2", ReadAscii(f.Heap, f.PoolBase + f.SuffixA, 2));
    }

    // ─── sweep-gate via the injected flag (B2) ─────────────────────────────────────

    [Fact]
    public void PoolPaint_true_with_full_coverage_skips_the_sweep_forever()
    {
        var f = BuildTwoWeaponPoolFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: true);

        for (int i = 0; i < 10; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }

        Assert.False(display._sweep.IsComplete);
        Assert.Equal(1, display._sweep.Generation);   // never advanced, Tick(budget, OnChunk) never ran
    }

    [Fact]
    public void PoolPaint_false_sweep_runs_and_generation_advances_like_before()
    {
        var f = BuildTwoWeaponPoolFixture();
        var clock = new TestClock();
        var display = CardFixtures.MakeDisplay(f.Meta, f.Kills, f.Heap, f.StaticsBase, clock, poolPaint: false);

        for (int i = 0; i < 10; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }
        Assert.True(display._sweep.IsComplete);   // the sweep ran a full pass, unlike the skip case above

        clock.Ms += DisplaySweep.GenerationRestMs + DisplaySweep.HotRescanMs + 10;
        display.Tick(false);

        Assert.Equal(2, display._sweep.Generation);   // a fresh generation started, regression guard
    }

    // ─── read-only pool (B1/premise-9): zero writes, sweep NOT skipped ─────────────

    [Fact]
    public void PoolPaint_readonly_pool_issues_zero_writes_and_never_skips_the_sweep()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret" } },
        };
        var kills = new Dictionary<int, int> { { 10, 7 } };
        var clock = new TestClock();

        var poolBuf = new byte[500];
        CardFixtures.WriteCardForwardWithName(poolBuf, 0, "BowX", "Fletched with regret");
        var poolBufSnapshot = (byte[])poolBuf.Clone();

        long poolBase = 0x54_0000_0000L;
        long staticsBase = 0x55_0000_0000L;
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;

        var heap = new FakeHeap((poolBase, poolBuf, false), (staticsBase, statics, true));   // read-only pool
        var display = CardFixtures.MakeDisplay(meta, kills, heap, staticsBase, clock, poolPaint: true);

        for (int i = 0; i < 30; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }

        // Read-only means the pool is absent from Regions() (matches production Mem.Regions()),
        // so the pool path never covers it: the sweep must have run instead of being skipped.
        Assert.True(display._sweep.IsComplete, "the sweep must run when the pool cannot be located");

        var current = heap.RegionBytes(poolBase)!;
        Assert.Equal(poolBufSnapshot, current);   // zero writes to the read-only region
    }
}
