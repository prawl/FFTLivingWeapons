using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardSites paint operations: basic paint, buffer-reuse regression, skip-if-equal.
/// </summary>
public class CardSitesPaintTests
{
    [Fact]
    public void Paints_the_owners_count_into_its_slot()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int pos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, "A fine blade", gap: 50);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);
        bool added = sites.Add(site);
        Assert.True(added);
        Assert.Equal(1, sites.Count);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 42 : 0);
        Assert.Equal(1, writes);

        bool ok = heap.TryReadBytes(0x1000 + slotAddr, 4, out var painted);
        Assert.True(ok);
        var expected = ByteScan.Ascii("42  ");
        Assert.Equal(expected, painted);
    }

    [Fact]
    public void Buffer_reuse_regression_skips_paint_when_anchor_changed()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int pos1 = 10;
        var flavorBytes1 = ByteScan.Ascii("A fine blade");
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos1, "A fine blade", gap: 50);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos1, IsKills: true);
        sites.Add(site);
        Assert.Equal(1, sites.Count);

        string flavor2 = "XXXXXXXXXXXX";
        var flavorBytes2 = ByteScan.Ascii(flavor2);
        heap.WriteBytes(0x1000 + pos1, flavorBytes2);

        Assert.True(heap.TryReadBytes(0x1000 + pos1, 12, out var verifyOverwrite));
        Assert.NotEqual(flavorBytes1, verifyOverwrite);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 42 : 0);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Skip_if_equal_paints_only_once()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int pos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, "A fine blade", gap: 50, slot: "42  ");

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);
        sites.Add(site);

        int writes1 = sites.Paint(new[] { site }, id => id == 1 ? 42 : 0);
        Assert.Equal(0, writes1);

        int writes2 = sites.Paint(new[] { site }, id => id == 1 ? 42 : 0);
        Assert.Equal(0, writes2);
    }
}
