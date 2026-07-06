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
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        int pos = 20;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, "A fine blade", gap: 100, enc: 2);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 2, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);
        sites.Add(site);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 99 : 0);
        Assert.Equal(1, writes);

        bool ok = heap.TryReadBytes(0x1000 + slotAddr, Signatures.KillsMeterSlotChars * 2, out var painted);
        Assert.True(ok);
        var expected = ByteScan.Utf16(Signatures.KillsMeterSlot(99));
        Assert.Equal(expected, painted);
    }

    [Fact]
    public void PaintAll_paints_all_cached_sites()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        int pos1 = 10;
        int slotAddr1 = CardFixtures.WriteKillsBlock(buf, pos1, "A fine blade", gap: 30);

        int pos2 = 150;
        int slotAddr2 = CardFixtures.WriteKillsBlock(buf, pos2, "A hefty tool", gap: 30);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site1 = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr1, AnchorAddr: 0x1000 + pos1, IsKills: true);
        var site2 = new CardSites.Site(Id: 2, Enc: 1, SlotAddr: 0x1000 + slotAddr2, AnchorAddr: 0x1000 + pos2, IsKills: true);

        sites.Add(site1);
        sites.Add(site2);

        int writes = sites.PaintAll(id => id == 1 ? 42 : (id == 2 ? 99 : 0));
        Assert.Equal(2, writes);

        int meterWidth = Signatures.KillsMeterSlotChars;
        bool ok1 = heap.TryReadBytes(0x1000 + slotAddr1, meterWidth, out var painted1);
        Assert.True(ok1);
        Assert.Equal(ByteScan.Ascii(Signatures.KillsMeterSlot(42)), painted1);

        bool ok2 = heap.TryReadBytes(0x1000 + slotAddr2, meterWidth, out var painted2);
        Assert.True(ok2);
        Assert.Equal(ByteScan.Ascii(Signatures.KillsMeterSlot(99)), painted2);
    }
}
