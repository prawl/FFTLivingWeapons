using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardSites suffix painting: tier suffix encoding and tier-zero paint.
/// </summary>
public class CardSitesSuffixTests
{
    [Fact]
    public void Suffix_site_paints_tier_suffix()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        string name = "Sword";
        string suffix = "  ";

        int pos = 10;
        var nameBytes = ByteScan.Ascii(name);
        var suffixBytes = ByteScan.Ascii(suffix);

        Array.Copy(nameBytes, 0, buf, pos, nameBytes.Length);
        int slotAddr = pos + nameBytes.Length;
        Array.Copy(suffixBytes, 0, buf, slotAddr, suffixBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: false);
        sites.Add(site);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 20 : 0);
        Assert.Equal(1, writes);

        bool ok = heap.TryReadBytes(0x1000 + slotAddr, 2, out var painted);
        Assert.True(ok);
        var expected = ByteScan.Ascii("+2");
        Assert.Equal(expected, painted);
    }

    [Fact]
    public void Suffix_site_tier_zero_paints_spaces_utf16()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        string name = "Sword";
        string suffix = "+ ";

        int pos = 10;
        var nameBytes = ByteScan.Utf16(name);
        var suffixBytes = ByteScan.Utf16(suffix);

        Array.Copy(nameBytes, 0, buf, pos, nameBytes.Length);
        int slotAddr = pos + nameBytes.Length;
        Array.Copy(suffixBytes, 0, buf, slotAddr, suffixBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 2, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: false);
        sites.Add(site);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 0 : 0);
        Assert.Equal(1, writes);

        bool ok = heap.TryReadBytes(0x1000 + slotAddr, 4, out var painted);
        Assert.True(ok);
        var expected = ByteScan.Utf16("  ");
        Assert.Equal(expected, painted);
    }
}
