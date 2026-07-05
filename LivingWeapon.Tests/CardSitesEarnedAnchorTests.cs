using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Reliquary Phase 1 decision 12 (the anchor-rotation rule) at the CardSites layer: a kills
/// site's anchor verify accepts ANY registered pattern (baked, current, or previous earned
/// line) -- never evicting a site painted with a stale-but-known line -- and every successful
/// verify repaints the anchor bytes to the CURRENT line when they differ, independent of
/// whatever the kills-count slot write decides (skip-if-equal, invalid digits, ...). No store
/// writes happen here -- painting never touches LegendStore.
/// </summary>
public class CardSitesEarnedAnchorTests
{
    private static Dictionary<int, WeaponMeta> Meta(string flavor = "A fine blade indeed.")   // 20 chars
        => new()
        {
            [1] = new WeaponMeta { Name = "Sword", Flavor = flavor, Wp = 15, Cat = "Sword", Formula = 1 },
        };

    private const string LineA = "Sword -- 5 felled.  ";   // 20 chars
    private const string LineB = "Sword -- 9 felled.  ";   // 20 chars

    [Fact]
    public void Stale_anchor_repainted_not_evicted()
    {
        var meta = Meta();
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);
        anchors.SetCurrent(1, LineA);

        var buf = new byte[200];
        int pos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, LineA, gap: 50);
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);

        // Rotate the composed line AFTER the site was painted with LineA -- the on-screen
        // buffer still literally holds LineA (untouched).
        anchors.SetCurrent(1, LineB);   // previous=LineA, current=LineB

        var sites = new CardSites(heap, pats, anchors);
        sites.Add(site);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 5 : 0);

        Assert.Equal(1, sites.Count);   // NOT evicted -- verified via the PREVIOUS anchor
        Assert.True(heap.TryReadBytes(0x1000 + pos, LineB.Length, out var repainted));
        Assert.Equal(ByteScan.Ascii(LineB), repainted);   // repainted to CURRENT
        Assert.True(writes >= 1);
    }

    [Fact]
    public void Repaint_fires_even_when_slot_write_skips()
    {
        var meta = Meta();
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);
        anchors.SetCurrent(1, LineA);

        var buf = new byte[200];
        int pos = 10;
        // Kills slot already shows "5   " -- the count-slot write will skip-if-equal.
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, LineA, gap: 50, slot: "5   ");
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);

        anchors.SetCurrent(1, LineB);   // rotates -- current differs from what's on screen

        var sites = new CardSites(heap, pats, anchors);
        sites.Add(site);
        sites.Paint(new[] { site }, id => id == 1 ? 5 : 0);   // killsFor==5 matches the slot -> NoWrite there

        Assert.True(heap.TryReadBytes(0x1000 + pos, LineB.Length, out var repainted));
        Assert.Equal(ByteScan.Ascii(LineB), repainted);   // the flavor repaint still happened
    }

    [Fact]
    public void Flavor_write_is_writable_gated_and_exact_length()
    {
        var meta = Meta();
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);
        anchors.SetCurrent(1, LineA);

        // Two SEPARATE regions: the flavor anchor lives in a READ-ONLY region; the kills slot
        // lives in a writable one elsewhere.
        var flavorBuf = ByteScan.Ascii(LineA);
        var flavorHeap = new FakeHeap((0x2000L, flavorBuf, writable: false));
        var slotBuf = new byte[64];
        int slotPos = 0;
        var prefixB = ByteScan.Ascii("Kills: ");
        System.Array.Copy(prefixB, 0, slotBuf, slotPos, prefixB.Length);
        int slotAddr = slotPos + prefixB.Length;
        System.Array.Copy(ByteScan.Ascii("5   "), 0, slotBuf, slotAddr, 4);

        var mem = new TwoRegionMem(flavorHeap, new FakeHeap((0x3000L, slotBuf, writable: true)));
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x3000 + slotAddr, AnchorAddr: 0x2000, IsKills: true);

        anchors.SetCurrent(1, LineB);   // current now differs from the read-only anchor's bytes

        var sites = new CardSites(mem, pats, anchors);
        sites.Add(site);
        var ex = Record.Exception(() => sites.Paint(new[] { site }, id => id == 1 ? 5 : 0));

        Assert.Null(ex);
        Assert.True(flavorHeap.TryReadBytes(0x2000, LineA.Length, out var stillA));
        Assert.Equal(ByteScan.Ascii(LineA), stillA);   // Writable(false) -- refused, never corrupted
    }

    [Fact]
    public void No_deeds_means_no_flavor_write()
    {
        var meta = Meta();
        var pats = new CardPatterns(meta);
        var anchors = new EarnedAnchors(pats);   // no SetCurrent ever called -- no deeds recorded

        var buf = new byte[200];
        int pos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, meta[1].Flavor, gap: 50);
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);

        var sites = new CardSites(heap, pats, anchors);
        sites.Add(site);
        sites.Paint(new[] { site }, id => id == 1 ? 5 : 0);

        Assert.True(heap.TryReadBytes(0x1000 + pos, meta[1].Flavor.Length, out var stillBaked));
        Assert.Equal(ByteScan.Ascii(meta[1].Flavor), stillBaked);   // untouched -- no CURRENT to repaint to
    }

    [Fact]
    public void Baked_only_verify_when_anchors_is_null()
    {
        // Every pre-Reliquary caller/test omits `anchors` entirely -- must behave byte-identically.
        var meta = Meta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int pos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, meta[1].Flavor, gap: 50);
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);

        var sites = new CardSites(heap, pats);   // anchors omitted -> null
        sites.Add(site);
        int writes = sites.Paint(new[] { site }, id => id == 1 ? 5 : 0);

        Assert.Equal(1, writes);   // count slot painted normally
        Assert.True(heap.TryReadBytes(0x1000 + pos, meta[1].Flavor.Length, out var stillBaked));
        Assert.Equal(ByteScan.Ascii(meta[1].Flavor), stillBaked);   // no flavor-sync attempted at all
    }

    /// <summary>Minimal IGameMemory that routes reads/writes to whichever of two FakeHeaps
    /// actually owns the address -- lets a test place the flavor anchor and the kills slot in
    /// TWO separate regions with different writability.</summary>
    private sealed class TwoRegionMem : IGameMemory
    {
        private readonly FakeHeap _a;
        private readonly FakeHeap _b;
        public TwoRegionMem(FakeHeap a, FakeHeap b) { _a = a; _b = b; }

        public byte U8(long addr) => _a.Readable(addr, 1) ? _a.U8(addr) : _b.U8(addr);
        public ushort U16(long addr) => _a.Readable(addr, 2) ? _a.U16(addr) : _b.U16(addr);
        public bool TryReadBytes(long addr, int len, out byte[] buf)
            => _a.Readable(addr, len) ? _a.TryReadBytes(addr, len, out buf) : _b.TryReadBytes(addr, len, out buf);
        public int ReadInto(long addr, byte[] buf, int len)
            => _a.Readable(addr, len) ? _a.ReadInto(addr, buf, len) : _b.ReadInto(addr, buf, len);
        public void WriteBytes(long addr, byte[] data)
        {
            if (_a.Readable(addr, data.Length)) _a.WriteBytes(addr, data);
            else _b.WriteBytes(addr, data);
        }
        public void W8(long addr, byte value) => WriteBytes(addr, new[] { value });
        public bool Readable(long addr, int len) => _a.Readable(addr, len) || _b.Readable(addr, len);
        public bool Writable(long addr, int len) => _a.Readable(addr, len) ? _a.Writable(addr, len) : _b.Writable(addr, len);
        public IEnumerable<(long baseAddr, long size)> Regions()
        {
            foreach (var r in _a.Regions()) yield return r;
            foreach (var r in _b.Regions()) yield return r;
        }
    }
}
