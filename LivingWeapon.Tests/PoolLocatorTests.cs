using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PoolLocator: locate + cache the writable UE string pool region(s) (LW-37). Two load-bearing
/// properties: (1) only NAME-bearing baked regions qualify (a name-less flavor+Kills render copy
/// is excluded, the live 2026-07-08 miss), and (2) EVERY qualifying region is returned, not just
/// one, because the process holds several name-bearing baked copies and the card materializes from one
/// of them with no static signature for which, so PoolPaint paints them all.
/// </summary>
public class PoolLocatorTests
{
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 1, new WeaponMeta { Name = "Sword", Flavor = "A sharp blade" } },
        { 2, new WeaponMeta { Name = "Staff", Flavor = "B holy relic" } },
    };

    /// <summary>A "Kills: " literal with NO owner flavor anywhere near it (no attribution
    /// possible), the shape a transient widget or a mid-transition buffer can present. Policy.Scan
    /// reports IsPool=false for this.</summary>
    private static byte[] BuildDecoyBuffer()
    {
        var parts = new List<byte>();
        parts.AddRange(ByteScan.Enc("Kills: ", 1));
        parts.AddRange(ByteScan.Enc(Signatures.KillsMeterSlot(0), 1));
        parts.AddRange(ByteScan.Enc("padding padding padding", 1));
        return parts.ToArray();
    }

    private static byte[] BuildPoolBuffer()
    {
        var buf = new byte[500];
        var (_, _, flavorA) = CardFixtures.WriteCardForwardWithName(buf, 0, "Sword", "A sharp blade", enc: 1);
        int nextStart = flavorA + ByteScan.Enc("A sharp blade", 1).Length + 20;
        CardFixtures.WriteCardForwardWithName(buf, nextStart, "Staff", "B holy relic", enc: 1);
        return buf;
    }

    /// <summary>The REAL transient-descriptor region shape (confirmed live 2026-07-08): EVERY
    /// weapon's flavor + "Kills: " (so it ties the pool's distinct-weapon count) but carrying NO
    /// names. Policy.Scan reports IsPool=false for this, so it is never a paint target.</summary>
    private static byte[] BuildNamelessAllItemsDecoy()
    {
        var buf = new byte[400];
        var (_, flavorA) = CardFixtures.WriteCardForward(buf, 0, "A sharp blade", enc: 1);
        int nextStart = flavorA + ByteScan.Enc("A sharp blade", 1).Length + 20;
        CardFixtures.WriteCardForward(buf, nextStart, "B holy relic", enc: 1);
        return buf;
    }

    // ─── LOAD-BEARING ───────────────────────────────────────────────────────────

    [Fact]
    public void LocateAll_returns_every_name_bearing_baked_region()
    {
        // The live LW-37 miss (2026-07-08): the process holds MULTIPLE name-bearing baked copies of
        // the descriptions; the card materializes from one of them with no static signature for
        // which. Picking only the "best" single region painted the wrong copy and left the card
        // baked. PoolPaint must paint ALL of them, so LocateAll must return them all.
        var pats = new CardPatterns(BuildMeta());
        long lowBase = 0x1000L;
        long highBase = 0x9000L;
        var heap = new FakeHeap((lowBase, BuildPoolBuffer(), true), (highBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        var regions = locator.LocateAll();

        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, r => r.baseAddr == lowBase);
        Assert.Contains(regions, r => r.baseAddr == highBase);
    }

    [Fact]
    public void LocateAll_excludes_a_nameless_all_items_decoy()
    {
        // Name-gate regression: a decoy that attributes every weapon's flavor+Kills but carries NO
        // names must NOT be a paint target (it is a transient render copy, not the baked pool).
        // Drop the name gate in PoolLocatorPolicy and this goes red (the nameless decoy is kept).
        var pats = new CardPatterns(BuildMeta());
        long decoyBase = 0x1000L;
        long poolBase = 0x9000L;
        var heap = new FakeHeap((decoyBase, BuildNamelessAllItemsDecoy(), true),
                                (poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        var regions = locator.LocateAll();

        Assert.Contains(regions, r => r.baseAddr == poolBase);
        Assert.DoesNotContain(regions, r => r.baseAddr == decoyBase);
    }

    [Fact]
    public void LocateAll_excludes_a_partial_no_flavor_decoy()
    {
        var pats = new CardPatterns(BuildMeta());
        long decoyBase = 0x1000L;
        long poolBase = 0x9000L;
        var heap = new FakeHeap((decoyBase, BuildDecoyBuffer(), true), (poolBase, BuildPoolBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        var regions = locator.LocateAll();

        Assert.Contains(regions, r => r.baseAddr == poolBase);
        Assert.DoesNotContain(regions, r => r.baseAddr == decoyBase);
    }

    [Fact]
    public void LocateAll_is_empty_when_only_partial_widget_regions_exist()
    {
        var pats = new CardPatterns(BuildMeta());
        var heap = new FakeHeap((0x1000L, BuildDecoyBuffer(), true), (0x2000L, BuildDecoyBuffer(), true));
        var locator = new PoolLocator(heap, pats);

        Assert.Empty(locator.LocateAll());
    }

    // ─── CACHE ──────────────────────────────────────────────────────────────────

    [Fact]
    public void LocateAll_cache_hit_does_not_rescan_Regions()
    {
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x9000L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var spy = new RegionsSpyMem(heap);
        var locator = new PoolLocator(spy, pats);

        locator.LocateAll();
        int callsAfterFirst = spy.RegionsCalls;
        Assert.True(callsAfterFirst > 0, "the first (cold) locate must have scanned Regions()");

        locator.LocateAll();

        Assert.Equal(callsAfterFirst, spy.RegionsCalls);   // hit path: no additional Regions() walk
    }

    [Fact]
    public void SeedForTest_drives_the_revalidate_path_without_a_prior_scan()
    {
        // Non-vacuity (mirrors GrowthEngine's SeedStructForSlotForTest tests): Regions() here yields
        // ONLY a decoy that is not the pool at all, so the ONLY way LocateAll can return the seeded
        // pool address is the cache revalidate path, never a fresh full scan.
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x9000L;
        var poolBuf = BuildPoolBuffer();
        var heap = new FakeHeap((0x1000L, BuildDecoyBuffer(), true), (poolBase, poolBuf, true));
        var locator = new PoolLocator(heap, pats);

        locator.SeedForTest((poolBase, poolBuf.Length));
        var regions = locator.LocateAll();

        Assert.Single(regions);
        Assert.Equal(poolBase, regions[0].baseAddr);
    }

    [Fact]
    public void Stale_seeded_region_misses_revalidate_and_falls_through_to_a_fresh_scan()
    {
        var pats = new CardPatterns(BuildMeta());
        long staleBase = 0x1000L;
        long realPoolBase = 0x9000L;
        var poolBuf = BuildPoolBuffer();
        // The seeded address now holds only the decoy shape (relocated/realloc'd away).
        var heap = new FakeHeap((staleBase, BuildDecoyBuffer(), true), (realPoolBase, poolBuf, true));
        var locator = new PoolLocator(heap, pats);

        locator.SeedForTest((staleBase, BuildDecoyBuffer().Length));
        var regions = locator.LocateAll();

        Assert.Contains(regions, r => r.baseAddr == realPoolBase);   // fell through to the real pool
        Assert.DoesNotContain(regions, r => r.baseAddr == staleBase);
    }

    [Fact]
    public void Invalidate_clears_the_cache_and_forces_a_rescan()
    {
        var pats = new CardPatterns(BuildMeta());
        long poolBase = 0x9000L;
        var heap = new FakeHeap((poolBase, BuildPoolBuffer(), true));
        var spy = new RegionsSpyMem(heap);
        var locator = new PoolLocator(spy, pats);

        locator.LocateAll();
        int callsAfterFirst = spy.RegionsCalls;

        locator.Invalidate();
        locator.LocateAll();

        Assert.True(spy.RegionsCalls > callsAfterFirst, "Invalidate must force the next locate to rescan");
    }

    // ─── READ-ONLY POOL (B1/premise-9) ────────────────────────────────────────

    [Fact]
    public void LocateAll_omits_a_readonly_pool_region()
    {
        var pats = new CardPatterns(BuildMeta());
        var heap = new FakeHeap((0x9000L, BuildPoolBuffer(), writable: false));
        var locator = new PoolLocator(heap, pats);

        Assert.Empty(locator.LocateAll());
    }

    /// <summary>IGameMemory wrapper that counts Regions() calls, forwarding everything else --
    /// proves a cache hit skips the region walk entirely rather than merely re-finding the
    /// same answer.</summary>
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
