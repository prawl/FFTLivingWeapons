using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Display v2: the orchestrator that ties CardPatterns, DisplaySweep, CardSites, and the
/// WpScratch write into a single, end-to-end paint pipeline. Regression tests lock in four
/// live bugs: (1) "shared kills" -- every weapon's counter updated independently of which
/// weapon is currently equipped; (2) "tier-0 never painted" -- the old tier-0 gate silently
/// suppressed counts below the first threshold; (3) mid-session kill update -- a kill bump
/// lands without re-equip; (4) invalidate/repopulate -- sites re-found correctly on call to
/// Invalidate(). Also covers suffix painting, WpScratch guarding, and budget enforcement.
/// </summary>
public class DisplayTests
{
    // â”€â”€â”€ Fixture helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private const long StaticsBase = 0x10_0000_0000L;
    private const long SourceBase  = 0x20_0000_0000L;

    /// <summary>Build a 3-weapon meta dict (ids 10, 11, 12) with unique names and flavors.</summary>
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "SwordA", Flavor = "Bright edge of dawn", Wp = 12, Cat = "Sword", Formula = 1 } },
        { 11, new WeaponMeta { Name = "SwordB", Flavor = "Cold iron remembers", Wp = 14, Cat = "Sword", Formula = 1 } },
        { 12, new WeaponMeta { Name = "SwordC", Flavor = "Rust never forgives", Wp = 10, Cat = "Sword", Formula = 1 } },
    };

    /// <summary>
    /// Build a FakeHeap with:
    /// - a source-blob region at SourceBase with three contiguous card blocks (ids 10, 11, 12)
    /// - a statics region at StaticsBase: u16 LE MirrorWeapon at +0, u16 LE MirrorOffHand at +2,
    ///   u8 WpScratch at +4
    /// </summary>
    private static (FakeHeap heap, byte[] source,
                    (int suffixPos, int flavorPos, int killsSlotPos) cardA,
                    (int suffixPos, int flavorPos, int killsSlotPos) cardB,
                    (int suffixPos, int flavorPos, int killsSlotPos) cardC)
        BuildFixture(int mirrorId, byte naturalWp)
    {
        var src = new byte[512];
        var cA = CardFixtures.WriteCard(src, 0,   "SwordA", "Bright edge of dawn");
        var cB = CardFixtures.WriteCard(src, 150, "SwordB", "Cold iron remembers");
        var cC = CardFixtures.WriteCard(src, 300, "SwordC", "Rust never forgives");

        var statics = new byte[64];
        // MirrorWeapon at offset 0 (u16 LE)
        statics[0] = (byte)(mirrorId & 0xFF);
        statics[1] = (byte)(mirrorId >> 8);
        // MirrorOffHand at offset 2 (u16 LE) = 0
        statics[2] = 0; statics[3] = 0;
        // WpScratch at offset 4 (u8)
        statics[4] = naturalWp;

        var heap = new FakeHeap(
            (SourceBase,  src),
            (StaticsBase, statics));

        return (heap, src, cA, cB, cC);
    }

    // â”€â”€â”€ helper: read back a slot from the FakeHeap source region â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static string ReadSlot(FakeHeap heap, long pos, int len)
    {
        heap.TryReadBytes(SourceBase + pos, len, out var buf);
        return System.Text.Encoding.ASCII.GetString(buf);
    }

    // â”€â”€â”€ (a) LIVE-BUG REGRESSION "shared kills" â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Shared_kills_regression_each_weapon_gets_its_own_count()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 9 }, { 11, 7 }, { 12, 0 } };
        var clock = new TestClock();
        var (heap, _, cA, cB, cC) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        // B (id 11) is NOT a mirror target but its count must still be painted correctly
        Assert.Equal("9   ", ReadSlot(heap, cA.killsSlotPos, 4));
        Assert.Equal("7   ", ReadSlot(heap, cB.killsSlotPos, 4));
        Assert.Equal("0   ", ReadSlot(heap, cC.killsSlotPos, 4));
    }

    // â”€â”€â”€ (b) LIVE-BUG REGRESSION "tier-0 never painted" â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Tier_zero_kills_paints_the_count_under_prod_thresholds()
    {
        // Prod thresholds {5,20,50}: 3 kills < 5 -> tier 0.
        // Old code gated on tier > 0 and silently skipped painting those weapons.
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 3 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();
        var (heap, _, cA, _, _) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal("3   ", ReadSlot(heap, cA.killsSlotPos, 4));
        // Suffix stays "  " (tier 0 under prod thresholds)
        Assert.Equal("  ", ReadSlot(heap, cA.suffixPos, 2));
    }

    // â”€â”€â”€ (c) Kill mid-session updates without re-equip â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Mid_session_kill_bump_repaints_count()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 5 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();
        var (heap, _, cA, _, _) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);
        Assert.Equal("5   ", ReadSlot(heap, cA.killsSlotPos, 4));

        // Bump kills without invalidating or re-equipping
        kills[10] = 8;
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        display.Tick(false);
        clock.Ms += DisplaySweep.HotRescanMs + 1;
        display.Tick(false);

        Assert.Equal("8   ", ReadSlot(heap, cA.killsSlotPos, 4));
    }

    // â”€â”€â”€ (d) Invalidate then re-populate â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Invalidate_then_tick_repaints_correct_counts()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 2 }, { 11, 3 }, { 12, 1 } };
        var clock = new TestClock();
        var (heap, _, cA, cB, cC) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        display.Invalidate();

        // Reset the heap kills slots to "0   " to verify they get repainted
        heap.WriteBytes(SourceBase + cA.killsSlotPos, ByteScan.Ascii("0   "));
        heap.WriteBytes(SourceBase + cB.killsSlotPos, ByteScan.Ascii("0   "));
        heap.WriteBytes(SourceBase + cC.killsSlotPos, ByteScan.Ascii("0   "));
        heap.WriteBytes(SourceBase + cA.suffixPos, ByteScan.Ascii("  "));

        // Must advance past GenerationMinGapMs for Invalidate to take effect
        clock.Ms += DisplaySweep.GenerationMinGapMs + 1;
        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal("2   ", ReadSlot(heap, cA.killsSlotPos, 4));
    }

    // â”€â”€â”€ (e) Suffix painting â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Suffix_target_weapon_at_tier2_gets_plus2_suffix()
    {
        var meta  = BuildMeta();
        // Prod thresholds {5,20,50}: 20 kills = tier 2 -> "+2"
        var kills = new Dictionary<int, int> { { 10, 25 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();
        var (heap, _, cA, _, _) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        Assert.Equal("+2", ReadSlot(heap, cA.suffixPos, 2));
    }

    [Fact]
    public void Non_target_suffix_slot_not_corrupted_by_other_weapons_count()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 50 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();
        var (heap, _, cA, cB, _) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        // B (id 11, 0 kills) suffix must be "  " (tier 0 or unchanged)
        Assert.Equal("  ", ReadSlot(heap, cB.suffixPos, 2));
        // B's kills slot must show 0, not A's count
        Assert.Equal("0   ", ReadSlot(heap, cB.killsSlotPos, 4));
    }

    // â”€â”€â”€ (f) WpScratch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void WpScratch_written_when_primed_with_natural_wp()
    {
        var meta  = BuildMeta();
        // id 10, Wp=12; 50 kills = tier 3 -> Factor=0.30 -> boosted = round(12*1.30)=16
        var kills = new Dictionary<int, int> { { 10, 50 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();
        var (heap, _, _, _, _) = BuildFixture(mirrorId: 10, naturalWp: 12);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        byte scratched = heap.U8(StaticsBase + 4);
        int expected = (int)Math.Round(12 * (1.0 + Tuning.Factor[3]));
        Assert.Equal((byte)expected, scratched);
    }

    [Fact]
    public void WpScratch_untouched_when_primed_with_unrelated_value()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 50 }, { 11, 0 }, { 12, 0 } };
        var clock = new TestClock();
        // Prime scratch with a value that is neither natural WP (12) nor the boosted value
        var (heap, _, _, _, _) = BuildFixture(mirrorId: 10, naturalWp: 99);
        var display = CardFixtures.MakeDisplay(meta, kills, heap, StaticsBase, clock);

        CardFixtures.DrainGeneration(display, clock, 500);

        byte scratched = heap.U8(StaticsBase + 4);
        Assert.Equal(99, (int)scratched);  // untouched
    }

    // â”€â”€â”€ (g) Budget sanity â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Single_tick_does_not_throw_against_multi_chunk_region()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 0 }, { 11, 0 }, { 12, 0 } };

        // Three-chunk region so the sweep has multiple chunks to walk
        long bigBase = 0x30_0000_0000L;
        int bigSize  = DisplaySweep.ChunkSize * 3;
        var bigData  = new byte[bigSize];
        var statics  = new byte[64];

        var heap = new FakeHeap((bigBase, bigData), (StaticsBase, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase + 0,
            mirrorOffHandAddr: StaticsBase + 2,
            wpScratchAddr:     StaticsBase + 4);

        var clock = new TestClock();
        var display = new Display(meta, kills, wrapped, clock.Func);

        // A single out-of-battle tick must not throw and must respect budget
        var ex = Record.Exception(() =>
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        });
        Assert.Null(ex);

        // A second tick also succeeds
        var ex2 = Record.Exception(() =>
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        });
        Assert.Null(ex2);
    }
}
