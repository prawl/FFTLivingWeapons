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
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int pos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, pos, "A fine blade", gap: 50);

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
        var meta = CardSitesFixtures.BuildMeta();
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
        var meta = CardSitesFixtures.BuildMeta();
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
        var meta = CardSitesFixtures.BuildMeta();
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
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        // Build a buffer with weapon 1's flavor and kills slot.
        var buf = new byte[300];
        int anchorPos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20);

        // Add a second weapon (id=2) with its own anchor somewhere else in the buffer.
        int anchor2Pos = 200;
        int slot2Addr = CardFixtures.WriteKillsBlock(buf, anchor2Pos, "A hefty tool", gap: 10);

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
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int anchorPos = 10;
        // Slot already holds the target meter body for kills=42.
        int slotAddr = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20, slot: Signatures.KillsMeterSlot(42));

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
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        int cap = CardSites.MaxSites;

        // Build a buffer large enough for cap distinct anchor positions (IsKills=false: only
        // the weapon NAME is checked, no kills-literal read needed).
        // The meta id=1 has Name = "Sword" (5 bytes via CardSitesFixtures).
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
        var meta = CardSitesFixtures.BuildMeta();
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
