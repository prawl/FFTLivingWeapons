using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-438. Every one of the 121 living weapons at +3 at once, on a cold launch and on a warm one,
/// measured against the suffix room (CardSites.MaxSuffixSites, SuffixCopiesPerId) and the sweep
/// rotation (SuffixRotation.RotationSlice). The fixture lays the whole card table out once per
/// copy, one region per copy, which is the live shape (PoolLocator keeps every baked copy).
/// </summary>
public class DisplayAllTieredSuffixTests
{
    private const int WeaponCount = 121;
    private const int TierThreeKills = 15;   // prod thresholds {5,10,15}

    private sealed record Fixture(FakeHeap Heap, long StaticsBase, Dictionary<int, WeaponMeta> Meta,
        Dictionary<int, int> Kills, long[] RegionBases, int[] RegionSizes,
        Dictionary<(int copy, int id), int> SuffixPos);

    private static string NameOf(int id) => $"Wpn{id:D3}X";

    /// <summary>121 weapons, ids 1..121, names the same length and distinct so FindAll cannot
    /// cross hit, one region per copy holding every card once. Kills default to tier 3 for all.</summary>
    private static Fixture Build(int copies, Func<int, int>? killsFor = null, int targetId = 1)
    {
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int>();
        for (int id = 1; id <= WeaponCount; id++)
        {
            meta[id] = new WeaponMeta { Name = NameOf(id), Flavor = $"Flavor line {id:D3} of the all tiered fixture" };
            kills[id] = killsFor?.Invoke(id) ?? TierThreeKills;
        }

        var bases = new long[copies];
        var sizes = new int[copies];
        var suffixPos = new Dictionary<(int, int), int>();
        var regions = new List<(long, byte[], bool)>();
        for (int c = 0; c < copies; c++)
        {
            var buf = new byte[16384];
            int pos = 0;
            for (int id = 1; id <= WeaponCount; id++)
            {
                var (suffix, _, flavorPos) = CardFixtures.WriteCardForwardWithName(buf, pos, NameOf(id), meta[id].Flavor!);
                suffixPos[(c, id)] = suffix;
                pos = flavorPos + ByteScan.Ascii(meta[id].Flavor!).Length + 16;
            }
            bases[c] = 0x70_0000_0000L + c * 0x1_0000_0000L;
            sizes[c] = buf.Length;
            regions.Add((bases[c], buf, true));
        }

        long staticsBase = 0x6F_0000_0000L;
        var statics = new byte[64];
        statics[0] = (byte)targetId;
        regions.Add((staticsBase, statics, true));

        var heap = new FakeHeap(regions.ToArray());
        return new Fixture(heap, staticsBase, meta, kills, bases, sizes, suffixPos);
    }

    private static string SuffixAt(Fixture f, int copy, int id)
    {
        f.Heap.TryReadBytes(f.RegionBases[copy] + f.SuffixPos[(copy, id)], 2, out var b);
        return System.Text.Encoding.ASCII.GetString(b);
    }

    private static int PaintedCopies(Fixture f, int copies, string expected)
    {
        int n = 0;
        for (int c = 0; c < copies; c++)
            for (int id = 1; id <= WeaponCount; id++)
                if (SuffixAt(f, c, id) == expected) n++;
        return n;
    }

    private static Display MakeDisplay(Fixture f, TestClock clock, bool poolPaint, string? sidecar = null)
    {
        var wrapped = new OffsetRemapMem(f.Heap, f.StaticsBase, f.StaticsBase + 2, f.StaticsBase + 4);
        return new Display(f.Meta, f.Kills, wrapped, clock.Func, legends: null, poolPaint: poolPaint,
                           recorder: null, poolRegionSidecarPath: sidecar);
    }

    // ─── the row's own scenario at the live copy count ────────────────────────────

    /// <summary>The 2026-08-18 owner tape held 484 to 487 suffix sites for 121 weapons, four
    /// copies of each name. At that count a cold launch with every weapon at +3 paints every copy
    /// on the first pool pass, nothing is dropped and the room is less than half used.</summary>
    [Fact]
    public void Cold_launch_with_every_weapon_at_plus_three_paints_every_copy_at_the_live_copy_count()
    {
        const int copies = 4;
        var f = Build(copies);
        var clock = new TestClock { Ms = 5_000_000 };
        var display = MakeDisplay(f, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        Assert.False(display._sweep.IsComplete);   // the pool path did the work, never the sweep
        Assert.True(display.PoolCoveredForTest);
        Assert.Equal(WeaponCount * copies, display._sites.SuffixCount);
        Assert.Equal(WeaponCount * copies, PaintedCopies(f, copies, "+3"));
    }

    /// <summary>A second launch warm starts from pool_regions.json. The seeded regions paint the
    /// same copies the cold launch did, so the sidecar is not what separates launch one from launch two.</summary>
    [Fact]
    public void Warm_start_from_the_region_sidecar_paints_the_same_copies_as_the_cold_launch()
    {
        const int copies = 4;
        var f = Build(copies);
        string sidecar = Path.Combine(Path.GetTempPath(), "lw438_pool_regions_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var persisted = new List<(long baseAddr, long size)>();
            for (int c = 0; c < copies; c++) persisted.Add((f.RegionBases[c], f.RegionSizes[c]));
            PoolRegionSidecar.Save(sidecar, persisted);

            var clock = new TestClock { Ms = 5_000_000 };
            var display = MakeDisplay(f, clock, poolPaint: true, sidecar);

            using var cap = LogCapture.Start();
            CardFixtures.TickWithPoolLocate(display, false);

            Assert.Contains(cap.File, l => l.Contains($"Warm start seeded {copies} of {copies} persisted pool regions"));
            Assert.False(display._sweep.IsComplete);
            Assert.Equal(WeaponCount * copies, PaintedCopies(f, copies, "+3"));
        }
        finally
        {
            if (File.Exists(sidecar)) File.Delete(sidecar);
        }
    }

    // ─── the size of the suffix room ──────────────────────────────────────────────

    /// <summary>121 names times 8 copies is 968, inside MaxSuffixSites (1024), so eight copies of
    /// every name all paint. This pins the room at eight copies per name.</summary>
    [Fact]
    public void The_suffix_room_holds_eight_copies_of_every_name()
    {
        const int copies = 8;
        Assert.True(WeaponCount * copies <= CardSites.MaxSuffixSites);
        var f = Build(copies);
        var clock = new TestClock { Ms = 5_000_000 };
        var display = MakeDisplay(f, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        Assert.Equal(WeaponCount * copies, display._sites.SuffixCount);
        Assert.Equal(WeaponCount * copies, PaintedCopies(f, copies, "+3"));
    }

    /// <summary>121 names times 9 copies is 1089, past the room. The copies found last are refused
    /// and stay bare, the count parks at the cap, and re equipping one of those weapons does not
    /// repaint it because a target change never clears the cache. Today's measured bound, the
    /// LW-438 row carries it; a cap resize flips this test.</summary>
    [Fact]
    public void Nine_copies_overflow_the_room_and_the_last_copies_stay_bare_even_after_re_equip()
    {
        const int copies = 9;
        Assert.True(WeaponCount * copies > CardSites.MaxSuffixSites);
        var f = Build(copies);
        var clock = new TestClock { Ms = 5_000_000 };
        var display = MakeDisplay(f, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        Assert.Equal(CardSites.MaxSuffixSites, display._sites.SuffixCount);
        Assert.Equal(CardSites.MaxSuffixSites, PaintedCopies(f, copies, "+3"));
        Assert.Equal("  ", SuffixAt(f, copies - 1, WeaponCount));

        // Re equip the bare weapon. Targets change, the cache does not, so the refused copy stays bare.
        f.Heap.WriteBytes(f.StaticsBase, new byte[] { (byte)WeaponCount, 0 });
        clock.Ms += Display.MaintenanceMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);
        clock.Ms += Display.MaintenanceMs + 1;
        CardFixtures.TickWithPoolLocate(display, false);

        Assert.Equal("  ", SuffixAt(f, copies - 1, WeaponCount));
    }

    /// <summary>The room fills with every tracked name, tiered or not, because the pool pass
    /// registers a suffix site for every weapon so a reset can downgrade it. So a player with
    /// two tiered weapons loses the same last copies as a player with 121.</summary>
    [Fact]
    public void Two_tiered_weapons_lose_the_same_last_copy_when_the_room_overflows()
    {
        const int copies = 9;
        var f = Build(copies, id => id == 1 || id == WeaponCount ? TierThreeKills : 0);
        var clock = new TestClock { Ms = 5_000_000 };
        var display = MakeDisplay(f, clock, poolPaint: true);

        CardFixtures.TickWithPoolLocate(display, false);

        Assert.Equal(CardSites.MaxSuffixSites, display._sites.SuffixCount);
        for (int c = 0; c < copies; c++) Assert.Equal("+3", SuffixAt(f, c, 1));
        for (int c = 0; c < copies - 1; c++) Assert.Equal("+3", SuffixAt(f, c, WeaponCount));
        Assert.Equal("  ", SuffixAt(f, copies - 1, WeaponCount));
    }

    // ─── no pool located, the sweep rotation alone ────────────────────────────────

    /// <summary>With no pool region located the whole heap sweep paints the target plus
    /// RotationSlice (8) names per visit of the hot chunk, a visit every HotRescanMs (250ms). 121
    /// names need the first pass plus 14 hot visits, about four seconds, then every name reads +3.</summary>
    [Fact]
    public void Without_a_pool_the_sweep_rotation_paints_all_121_names_within_fifteen_visits()
    {
        const int copies = 1;
        var f = Build(copies);
        var clock = new TestClock { Ms = 5_000_000 };
        var display = MakeDisplay(f, clock, poolPaint: false);

        CardFixtures.TickWithPoolLocate(display, false);
        Assert.True(display._sweep.IsComplete);
        Assert.Equal(1 + SuffixRotation.RotationSlice, PaintedCopies(f, copies, "+3"));

        int visits = (WeaponCount - 1 + SuffixRotation.RotationSlice - 1) / SuffixRotation.RotationSlice;   // 15
        for (int v = 1; v < visits; v++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            CardFixtures.TickWithPoolLocate(display, false);
        }

        Assert.Equal(WeaponCount, PaintedCopies(f, copies, "+3"));
    }
}
