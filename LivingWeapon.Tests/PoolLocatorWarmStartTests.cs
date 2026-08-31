using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-324: the warm-start seed (PoolRegionSidecar -> PoolLocator._cached, before the ordinary
/// cold scan begins) plus the sidecar's own write-on-completion. Fixture idioms mirror
/// PoolLocatorTests.cs/PoolLocatorStepTests.cs (BuildMeta, BuildPoolBuffer, BuildDecoyBuffer,
/// a large filler region to force several Step calls before a scan completes).
///
/// THE non-vacuous pair (one fixture, two persisted regions): a STALE one whose live memory no
/// longer verifies as pool content (relocated/realloc'd away, the same shape
/// PoolLocatorTests.Stale_seeded_region_misses_revalidate_and_falls_through_to_a_fresh_scan
/// pins for SeedForTest) and an INTACT one that still does. The stale region must never be
/// seeded; the intact one must be seeded immediately (before the cold scan makes any progress);
/// and -- THE TRAP TEST -- the seed must never mark the locate complete: the ordinary full scan
/// still runs against LIVE Regions() and republishes on its own, finding the intact region for
/// real and still excluding the stale one.
/// </summary>
public class PoolLocatorWarmStartTests
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

    /// <summary>A "Kills: " literal with no owner flavor/name anywhere near it -- not the baked
    /// pool shape (PoolLocatorTests.cs's own BuildDecoyBuffer). Standing in for a persisted
    /// region whose live memory has since relocated/realloc'd away from the pool it used to be.</summary>
    private static byte[] BuildDecoyBuffer()
    {
        var parts = new List<byte>();
        parts.AddRange(ByteScan.Enc("Kills: ", 1));
        parts.AddRange(ByteScan.Enc(Signatures.KillsMeterSlot(0), 1));
        parts.AddRange(ByteScan.Enc("padding padding padding", 1));
        return parts.ToArray();
    }

    [Fact]
    public void Warm_start_seeds_only_the_still_valid_region_and_the_full_scan_still_completes_and_republishes()
    {
        var pats = new CardPatterns(BuildMeta());
        long intactBase = 0x50_0000_0000L;
        long staleBase = 0x51_0000_0000L;
        long fillerBase = 0x52_0000_0000L;
        var intactBuf = BuildPoolBuffer();
        var staleLiveBuf = BuildDecoyBuffer();       // what LIVE memory holds at staleBase now
        var filler = new byte[12 * 1024 * 1024];     // > LocateBudgetInBattle: forces several Step calls

        var heap = new FakeHeap(
            (intactBase, intactBuf, true),
            (staleBase, staleLiveBuf, true),
            (fillerBase, filler, true));

        using var temp = TempDirs.Create("lw_poolwarm_");
        string sidecarPath = Path.Combine(temp.Dir, "pool_regions.json");
        // Persisted from a PRIOR launch: both regions, sized as they were back then (the stale
        // one's persisted size is its own old buffer's length -- irrelevant to how it fails the
        // reverify, since the live bytes there no longer parse as pool content at all).
        PoolRegionSidecar.Save(sidecarPath, new List<(long baseAddr, long size)>
        {
            (intactBase, intactBuf.Length),
            (staleBase, staleLiveBuf.Length),
        });

        var locator = new PoolLocator(heap, pats, regionSidecarPath: sidecarPath);

        // ── Phase 1: the very first Step seeds the survivor before the cold scan can progress ──
        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);

        Assert.Null(first);   // the cold scan itself has not completed (filler forces more Steps)
        Assert.Single(locator.CachedRegions);
        Assert.Equal(intactBase, locator.CachedRegions[0].baseAddr);
        Assert.DoesNotContain(locator.CachedRegions, r => r.baseAddr == staleBase);
        Assert.Equal(1, locator.PublishGeneration);   // the seed's own bump -- ScanPoolRegion
                                                       // (Display.PoolPaintCadence's ShouldRunFullPoolScan)
                                                       // treats this as a fresh publish on the very
                                                       // next paint call, with zero full-locate progress.

        // ── Phase 2 (the trap test): the seed never marks the locate complete -- keep stepping
        // and the ordinary full scan must still run against LIVE memory and republish on its own. ──
        PoolLocator.LocateCompletion? completion = null;
        for (int i = 0; i < 20 && completion == null; i++)
        {
            now += 33;
            completion = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        }

        Assert.NotNull(completion);                 // the full locate really did run to completion
        Assert.Equal("first", completion!.Value.Trigger);
        Assert.Contains(locator.CachedRegions, r => r.baseAddr == intactBase);   // rediscovered for real
        Assert.DoesNotContain(locator.CachedRegions, r => r.baseAddr == staleBase);   // never a match
        Assert.True(locator.PublishGeneration > 1, "the full scan's own publish must republish, not get stuck at the seed's generation");
    }

    [Fact]
    public void Adoption_log_line_reports_zero_of_zero_when_no_sidecar_exists_yet()
    {
        // Cold boot 1's own shape: nothing persisted yet. N=0 of M=0 must still log (the
        // adoption measurement the ledger row owes), not be silently skipped.
        var pats = new CardPatterns(BuildMeta());
        using var temp = TempDirs.Create("lw_poolwarm_");
        string sidecarPath = Path.Combine(temp.Dir, "pool_regions.json");
        var heap = new FakeHeap((0x9000L, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats, regionSidecarPath: sidecarPath);

        using var cap = LogCapture.Start();
        locator.Step(PoolLocator.LocateBudgetInBattle, 0);

        Assert.Contains(cap.File, l => l.Contains("Warm start seeded 0 of 0 persisted pool regions"));
    }

    [Fact]
    public void Adoption_log_line_reports_a_nonzero_N_of_M()
    {
        var pats = new CardPatterns(BuildMeta());
        using var temp = TempDirs.Create("lw_poolwarm_");
        string sidecarPath = Path.Combine(temp.Dir, "pool_regions.json");
        long poolBase = 0x9000L;
        var poolBuf = BuildPoolBuffer();
        PoolRegionSidecar.Save(sidecarPath, new List<(long baseAddr, long size)> { (poolBase, poolBuf.Length) });
        var heap = new FakeHeap((poolBase, poolBuf, true));
        var locator = new PoolLocator(heap, pats, regionSidecarPath: sidecarPath);

        using var cap = LogCapture.Start();
        locator.Step(PoolLocator.LocateBudgetInBattle, 0);

        Assert.Contains(cap.File, l => l.Contains("Warm start seeded 1 of 1 persisted pool regions"));
    }

    [Fact]
    public void A_locate_completion_writes_the_sidecar()
    {
        var pats = new CardPatterns(BuildMeta());
        using var temp = TempDirs.Create("lw_poolwarm_");
        string sidecarPath = Path.Combine(temp.Dir, "pool_regions.json");
        long poolBase = 0x9500L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats, regionSidecarPath: sidecarPath);

        Assert.False(File.Exists(sidecarPath));   // nothing written until a locate completes

        var completion = locator.Step(PoolLocator.LocateBudgetInBattle, 0);

        Assert.NotNull(completion);   // tiny fixture completes in one call
        Assert.True(File.Exists(sidecarPath), "a locate completion must write the sidecar");
        var loaded = PoolRegionSidecar.Load(sidecarPath);
        Assert.Single(loaded.Regions);
        Assert.Equal(poolBase, loaded.Regions[0].baseAddr);
    }

    [Fact]
    public void Save_on_completion_is_dirty_checked_no_second_write_when_regions_are_unchanged()
    {
        var pats = new CardPatterns(BuildMeta());
        using var temp = TempDirs.Create("lw_poolwarm_");
        string sidecarPath = Path.Combine(temp.Dir, "pool_regions.json");
        long poolBase = 0x9600L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats, regionSidecarPath: sidecarPath);

        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.NotNull(first);
        Assert.True(File.Exists(sidecarPath));
        // SidecarJson.SaveAtomic only ever creates the ".bak" sibling when a PRIOR primary
        // existed to back up -- the very first save has none, so its absence here is the
        // baseline a second, SKIPPED save must also leave untouched.
        Assert.False(File.Exists(sidecarPath + ".bak"));

        // The common battle-exit-Invalidate case: nothing actually moved, so the rescan finds
        // the exact same region again.
        locator.Invalidate();
        now += 33;
        var second = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.NotNull(second);

        Assert.False(File.Exists(sidecarPath + ".bak"),
            "an unchanged region list must not trigger a second write (the dirty check)");
    }

    [Fact]
    public void Save_on_completion_writes_again_when_the_region_list_actually_changed()
    {
        var pats = new CardPatterns(BuildMeta());
        using var temp = TempDirs.Create("lw_poolwarm_");
        string sidecarPath = Path.Combine(temp.Dir, "pool_regions.json");
        long poolBase = 0x9700L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats, regionSidecarPath: sidecarPath);

        long now = 0;
        var first = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.NotNull(first);
        Assert.False(File.Exists(sidecarPath + ".bak"));

        // A genuinely NEW region appears before the next completion -- proves the dirty check
        // detects a real change rather than simply never writing again.
        long secondPoolBase = 0x9800L;
        heap.AddRegion(secondPoolBase, BuildPoolBuffer(), writable: true);
        locator.Invalidate();
        now += 33;
        var second = locator.Step(PoolLocator.LocateBudgetInBattle, now);
        Assert.NotNull(second);
        Assert.Equal(2, second!.Value.Regions);

        Assert.True(File.Exists(sidecarPath + ".bak"),
            "a genuinely changed region list must trigger a real second write");
    }
}
