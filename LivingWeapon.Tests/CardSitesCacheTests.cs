using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardSites cache management: dedup, cap, utf16 round-trip, clear.
/// </summary>
public class CardSitesCacheTests
{
    [Fact]
    public void Read_only_region_issues_no_write()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        string flavor = "A fine blade";
        string killsPrefix = "Kills: ";
        string killsSlot = "0   ";

        int pos = 10;
        var flavorBytes = ByteScan.Ascii(flavor);
        var prefixBytes = ByteScan.Ascii(killsPrefix);
        var currentSlotBytes = ByteScan.Ascii(killsSlot);

        Array.Copy(flavorBytes, 0, buf, pos, flavorBytes.Length);
        int killsAddr = pos + flavorBytes.Length + 50;
        Array.Copy(prefixBytes, 0, buf, killsAddr, prefixBytes.Length);
        int slotAddr = killsAddr + prefixBytes.Length;
        Array.Copy(currentSlotBytes, 0, buf, slotAddr, currentSlotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: false));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + pos, IsKills: true);
        sites.Add(site);

        int writes = sites.Paint(new[] { site }, id => id == 1 ? 42 : 0);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Dedup_same_id_slot_enc_iskills_drops_duplicate()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site1 = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1100, IsKills: true);
        var site1b = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1100, IsKills: true);

        Assert.True(sites.Add(site1));
        Assert.Equal(1, sites.Count);

        Assert.False(sites.Add(site1b));
        Assert.Equal(1, sites.Count);
    }

    [Fact]
    public void Dedup_different_id_same_slot_is_NOT_dropped()
    {
        // B1: buffer reuse -- the game reused the UI buffer for a DIFFERENT weapon card.
        // The new weapon has the same SlotAddr but a different Id. The old site stays until
        // its anchor verify evicts it; the new owner site MUST be admitted so it can paint.
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site1 = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1100, IsKills: true);
        var site2 = new CardSites.Site(Id: 2, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1200, IsKills: true);

        Assert.True(sites.Add(site1));
        Assert.Equal(1, sites.Count);

        // Different Id -> must be admitted, not dropped as a duplicate.
        Assert.True(sites.Add(site2));
        Assert.Equal(2, sites.Count);
    }

    [Fact]
    public void Dedup_different_enc_same_slot_is_NOT_dropped()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site1 = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1100, IsKills: true);
        var site3 = new CardSites.Site(Id: 1, Enc: 2, SlotAddr: 0x1000, AnchorAddr: 0x1300, IsKills: true);

        Assert.True(sites.Add(site1));
        Assert.True(sites.Add(site3));
        Assert.Equal(2, sites.Count);
    }

    [Fact]
    public void Anchor_mismatch_evicts_site_after_one_PaintAll_pass()
    {
        // B1: a stale site (buffer was reused for a different card) is evicted when its
        // anchor verification fails during PaintAll. The new owner paints normally;
        // only the stale site is removed (Count decreases by exactly 1).
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        // Build a buffer with weapon 1's flavor and kills slot.
        var buf = new byte[300];
        string flavor1 = "A fine blade";
        string killsPrefix = "Kills: ";
        string killsSlot = "0   ";

        int anchorPos = 10;
        var flavorBytes1 = ByteScan.Ascii(flavor1);
        var prefixBytes = ByteScan.Ascii(killsPrefix);
        var currentSlotBytes = ByteScan.Ascii(killsSlot);

        Array.Copy(flavorBytes1, 0, buf, anchorPos, flavorBytes1.Length);
        int killsAddr = anchorPos + flavorBytes1.Length + 20;
        Array.Copy(prefixBytes, 0, buf, killsAddr, prefixBytes.Length);
        int slotAddr = killsAddr + prefixBytes.Length;
        Array.Copy(currentSlotBytes, 0, buf, slotAddr, currentSlotBytes.Length);

        // Add a second weapon (id=2) with its own anchor somewhere else in the buffer.
        string flavor2 = "A hefty tool";
        int anchor2Pos = 200;
        var flavorBytes2 = ByteScan.Ascii(flavor2);
        Array.Copy(flavorBytes2, 0, buf, anchor2Pos, flavorBytes2.Length);
        int kills2Addr = anchor2Pos + flavorBytes2.Length + 10;
        Array.Copy(prefixBytes, 0, buf, kills2Addr, prefixBytes.Length);
        int slot2Addr = kills2Addr + prefixBytes.Length;
        Array.Copy(currentSlotBytes, 0, buf, slot2Addr, currentSlotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var staleSite = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr,
            AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        var goodSite = new CardSites.Site(Id: 2, Enc: 1, SlotAddr: 0x1000 + slot2Addr,
            AnchorAddr: 0x1000 + anchor2Pos, IsKills: true);

        sites.Add(staleSite);
        sites.Add(goodSite);
        Assert.Equal(2, sites.Count);

        // Corrupt the anchor of staleSite so verification fails.
        heap.WriteBytes(0x1000 + anchorPos, ByteScan.Ascii("XXXXXXXXXXXX"));

        // PaintAll should evict the stale site and keep the good one.
        sites.PaintAll(id => 5);

        Assert.Equal(1, sites.Count);
    }

    [Fact]
    public void Skip_if_equal_does_NOT_evict_the_site()
    {
        // B1: skip-if-equal returns false (no write) but must NOT evict the site.
        // The anchor is valid and the slot already holds the correct value.
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        string flavor = "A fine blade";
        string killsPrefix = "Kills: ";
        string killsSlot = "42  ";   // already the target value for kills=42

        int anchorPos = 10;
        var flavorBytes = ByteScan.Ascii(flavor);
        var prefixBytes = ByteScan.Ascii(killsPrefix);
        var slotBytes = ByteScan.Ascii(killsSlot);

        Array.Copy(flavorBytes, 0, buf, anchorPos, flavorBytes.Length);
        int killsAddr = anchorPos + flavorBytes.Length + 20;
        Array.Copy(prefixBytes, 0, buf, killsAddr, prefixBytes.Length);
        int slotAddr = killsAddr + prefixBytes.Length;
        Array.Copy(slotBytes, 0, buf, slotAddr, slotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr,
            AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);
        Assert.Equal(1, sites.Count);

        // Skip-if-equal: already 42, target is 42.
        int writes = sites.PaintAll(id => 42);
        Assert.Equal(0, writes);

        // Site must survive intact.
        Assert.Equal(1, sites.Count);
    }

    [Fact]
    public void Cap_at_MaxSites_live_anchors_refused()
    {
        // Prune-on-cap evicts DEAD sites (unreadable anchor).  When ALL cached sites are
        // LIVE (readable anchor that matches), prune finds nothing to evict and the cap
        // still refuses the new site.  This verifies that MaxSites is the true upper bound
        // on the number of simultaneously-live cached sites.
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        int cap = CardSites.MaxSites;

        // Build a buffer large enough for cap distinct anchor positions (IsKills=false: only
        // the weapon NAME is checked, no kills-literal read needed).
        // The meta id=1 has Name = "Sword" (5 bytes via CardSitesTestBase).
        byte[] nameBytes   = ByteScan.Ascii("Sword"); // Name for id=1
        int    anchorStride = nameBytes.Length + 4;   // stride > name length
        int    bufSize      = cap * anchorStride + 256;
        var    buf          = new byte[bufSize];

        // Write the name bytes at every anchor position so all anchor reads succeed.
        for (int i = 0; i < cap; i++)
            Array.Copy(nameBytes, 0, buf, i * anchorStride, nameBytes.Length);

        var heap  = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        // Fill to cap with live-anchor suffix sites (distinct SlotAddr/AnchorAddr, no dedup).
        for (int i = 0; i < cap; i++)
        {
            long slotAddr   = 0x2000_0000L + i;      // distinct; slot not read at Add time
            long anchorAddr = 0x1000L + i * anchorStride;
            var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: slotAddr, AnchorAddr: anchorAddr, IsKills: false);
            Assert.True(sites.Add(site), $"slot {i} must be accepted");
        }
        Assert.Equal(cap, sites.Count);

        // Prune fires on the first cap-hit but finds nothing to evict (all anchors live).
        var overSite = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x2000_0000L + cap,
            AnchorAddr: 0x1000L, IsKills: false);
        Assert.False(sites.Add(overSite), "must refuse when all sites are live");
        Assert.Equal(cap, sites.Count);
    }

    [Fact]
    public void Clear_empties_the_cache()
    {
        var meta = CardSitesTestBase.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1100, IsKills: true);
        sites.Add(site);
        Assert.Equal(1, sites.Count);

        sites.Clear();
        Assert.Equal(0, sites.Count);
    }
}
