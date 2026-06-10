using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardSites UTF-16 and multi-site painting round-trip tests.
/// </summary>
public class CardSitesRoundTripTests
{
    [Fact]
    public void Utf16_site_round_trip()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        string flavor = "A fine blade";
        string killsPrefix = "Kills: ";
        string killsSlot = "0   ";

        int pos = 20;
        var flavorBytes = ByteScan.Utf16(flavor);
        var prefixBytes = ByteScan.Utf16(killsPrefix);
        var currentSlotBytes = ByteScan.Utf16(killsSlot);

        Array.Copy(flavorBytes, 0, buf, pos, flavorBytes.Length);
        int killsAddr = pos + flavorBytes.Length + 100;
        Array.Copy(prefixBytes, 0, buf, killsAddr, prefixBytes.Length);
        int slotAddr = killsAddr + prefixBytes.Length;
        Array.Copy(currentSlotBytes, 0, buf, slotAddr, currentSlotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 2, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);
        sites.Add(site);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 99 : 0);
        Assert.Equal(1, writes);

        bool ok = heap.TryReadBytes(0x1000 + slotAddr, 8, out var painted);
        Assert.True(ok);
        var expected = ByteScan.Utf16("99  ");
        Assert.Equal(expected, painted);
    }

    [Fact]
    public void PaintAll_paints_all_cached_sites()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        string flavor1 = "A fine blade";
        string flavor2 = "A hefty tool";
        string killsPrefix = "Kills: ";
        string killsSlot = "0   ";

        int pos1 = 10;
        var flavorBytes1 = ByteScan.Ascii(flavor1);
        var prefixBytes = ByteScan.Ascii(killsPrefix);
        var currentSlotBytes = ByteScan.Ascii(killsSlot);

        Array.Copy(flavorBytes1, 0, buf, pos1, flavorBytes1.Length);
        int killsAddr1 = pos1 + flavorBytes1.Length + 30;
        Array.Copy(prefixBytes, 0, buf, killsAddr1, prefixBytes.Length);
        int slotAddr1 = killsAddr1 + prefixBytes.Length;
        Array.Copy(currentSlotBytes, 0, buf, slotAddr1, currentSlotBytes.Length);

        int pos2 = 150;
        var flavorBytes2 = ByteScan.Ascii(flavor2);
        Array.Copy(flavorBytes2, 0, buf, pos2, flavorBytes2.Length);
        int killsAddr2 = pos2 + flavorBytes2.Length + 30;
        Array.Copy(prefixBytes, 0, buf, killsAddr2, prefixBytes.Length);
        int slotAddr2 = killsAddr2 + prefixBytes.Length;
        Array.Copy(currentSlotBytes, 0, buf, slotAddr2, currentSlotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site1 = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr1, AnchorAddr: 0x1000 + pos1, IsKills: true);
        var site2 = new CardSites.Site(Id: 2, Enc: 1, SlotAddr: 0x1000 + slotAddr2, AnchorAddr: 0x1000 + pos2, IsKills: true);

        sites.Add(site1);
        sites.Add(site2);

        int writes = sites.PaintAll(id => id == 1 ? 42 : (id == 2 ? 99 : 0));
        Assert.Equal(2, writes);

        bool ok1 = heap.TryReadBytes(0x1000 + slotAddr1, 4, out var painted1);
        Assert.True(ok1);
        Assert.Equal(ByteScan.Ascii("42  "), painted1);

        bool ok2 = heap.TryReadBytes(0x1000 + slotAddr2, 4, out var painted2);
        Assert.True(ok2);
        Assert.Equal(ByteScan.Ascii("99  "), painted2);
    }
}
