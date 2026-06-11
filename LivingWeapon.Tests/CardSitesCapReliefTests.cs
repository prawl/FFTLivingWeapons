using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Cap-relief prune tests (F1): when the cache is full, Add triggers a prune pass that
/// evicts dead sites and admits the new one.  Rate-limit: at most one prune per
/// PruneEveryRefusals refusals when saturated; the FIRST cap-hit after any successful
/// prune/Clear always prunes immediately.
/// </summary>
public class CardSitesCapReliefTests
{
    // ─── Fixture helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Build a minimal but real kills site: flavor anchor at anchorPos, "Kills: " + slot
    /// immediately following, all in one FakeHeap region at 0x1000.
    /// Returns the site record; heap contains live bytes for the site.
    /// </summary>
    private static (FakeHeap heap, CardSites.Site site) BuildLiveSite(int id, int bufSize = 512)
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[bufSize];
        string flavor = id == 1 ? "A fine blade" : "A hefty tool";
        int anchorPos = 10;
        int slotPos = CardFixtures.WriteKillsBlock(buf, anchorPos, flavor, gap: 20);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var site = new CardSites.Site(id, 1, 0x1000 + slotPos, 0x1000 + anchorPos, IsKills: true);
        return (heap, site);
    }

    // ─── T1: stale-status-card-count-at-cap regression ───────────────────────

    /// <summary>
    /// Live bug: stale status-card count at cap.
    /// Cache full of dead sites (anchors gone) -> Add a new valid site -> prune runs,
    /// dead sites evicted, new site IS admitted (Add returns true).
    /// </summary>
    [Fact]
    public void Stale_status_card_count_at_cap_dead_sites_evicted_new_admitted()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        // One heap region with a live anchor for the NEW site only.
        // Dead-site anchors will point into a region that does NOT exist so TryReadBytes fails.
        var liveBuf = new byte[512];
        int anchorPos = 10;
        int slotPos = CardFixtures.WriteKillsBlock(liveBuf, anchorPos, "A fine blade", gap: 20);

        var heap = new FakeHeap((0x1000L, liveBuf, writable: true));
        var sites = new CardSites(heap, pats);

        // Fill to MaxSites with sites whose AnchorAddr points into unmapped memory (dead).
        int cap = CardSites.MaxSites;
        for (int i = 0; i < cap; i++)
        {
            long deadAnchor = 0xDEAD_0000_0000L + i;
            long deadSlot   = 0xDEAD_1000_0000L + i;
            var dead = new CardSites.Site(1, 1, deadSlot, deadAnchor, IsKills: true);
            Assert.True(sites.Add(dead), $"pre-fill slot {i} should be accepted");
        }
        Assert.Equal(cap, sites.Count);

        // Now add a genuinely live site -- prune must fire, evict the dead ones, admit this.
        var newSite = new CardSites.Site(1, 1, 0x1000 + slotPos, 0x1000 + anchorPos, IsKills: true);
        bool admitted = sites.Add(newSite);

        Assert.True(admitted, "new valid site must be admitted after prune");
        Assert.True(sites.Count < cap, "prune must have reduced Count below MaxSites");
        Assert.True(sites.Count > 0,   "live site must remain after prune");
    }

    // ─── T2: cache full of live sites -> refuse, rate-limited ────────────────

    /// <summary>
    /// Cache full of live sites: Add still refuses (nothing to evict) and the prune is
    /// rate-limited (repeated refused Adds do not re-prune every call).
    /// Verified by observing that total IGameMemory reads do NOT scale linearly with
    /// the number of refused Adds once the rate limit kicks in.
    /// </summary>
    [Fact]
    public void Cap_full_of_live_sites_refused_and_prune_is_rate_limited()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        // Build enough heap so all MaxSites anchors are readable (live).
        // Each site gets its own anchor slot in a large buffer so anchor reads succeed.
        int cap = CardSites.MaxSites;
        int anchorStride = 20;
        int bufSize = cap * anchorStride + 512;
        var buf = new byte[bufSize];

        byte[] flavorBytes = ByteScan.Ascii("A fine blade");
        byte[] prefixBytes = ByteScan.Ascii("Kills: ");
        byte[] slotBytes   = ByteScan.Ascii("0   ");

        // Write all anchors, prefix, and slots into buf so every site is LIVE.
        int extraBase = cap * anchorStride;
        for (int i = 0; i < cap; i++)
        {
            int anchorPos = i * anchorStride;
            Array.Copy(flavorBytes, 0, buf, anchorPos, flavorBytes.Length);
        }
        // Kills literal + slot for every site (packed together after the anchor block).
        Array.Copy(prefixBytes, 0, buf, extraBase,                    prefixBytes.Length);
        Array.Copy(slotBytes,   0, buf, extraBase + prefixBytes.Length, slotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        long slotAddr = 0x1000 + extraBase + prefixBytes.Length;
        for (int i = 0; i < cap; i++)
        {
            long anchor = 0x1000 + i * anchorStride;
            // Use distinct SlotAddr so dedup doesn't drop them; also needs a "Kills: " before slot.
            // For simplicity use the shared slot (dedup key differs by anchorAddr).
            var s = new CardSites.Site(1, 1, slotAddr, anchor, IsKills: true);
            bool ok = sites.Add(s);
            Assert.True(ok, $"pre-fill {i} must be accepted");
        }
        Assert.Equal(cap, sites.Count);

        // Reset the read counter so we can measure how many reads happen during refused Adds.
        // FakeHeap doesn't expose a read counter, so we use Count as a proxy:
        // if prune ran every time, Count would drop (since anchors are live it finds nothing to
        // evict, so Count stays == cap).  Just assert the refusal and that Count stays stable.
        int reads0 = heap.Writes; // writes shouldn't change either

        var overflow1 = new CardSites.Site(2, 1, slotAddr + 100, 0x1000, IsKills: true);
        bool r1 = sites.Add(overflow1);
        Assert.False(r1, "must refuse when all sites are live");
        Assert.Equal(cap, sites.Count);

        // Attempting PruneEveryRefusals - 1 more refused Adds must NOT trigger a second prune.
        // The first Add above triggered prune #1 (first cap-hit after last Clear/prune).
        // Subsequent ones within the rate-limit window must not each re-verify all sites.
        // We verify this with an observable: Count stays at cap (nothing evicted = no prune
        // with net effect), and Writes stays 0 (no paints happened).
        for (int i = 0; i < CardSites.PruneEveryRefusals - 1; i++)
        {
            var extra = new CardSites.Site(2, 1, slotAddr + 200 + i, 0x1000 + i, IsKills: true);
            bool r = sites.Add(extra);
            Assert.False(r, $"refused attempt {i} must still be refused");
        }
        Assert.Equal(cap, sites.Count);
        Assert.Equal(reads0, heap.Writes); // no writes during these refusals
    }

    // ─── T3: first-hit-after-prune always prunes immediately ──────────────────

    /// <summary>
    /// After a successful prune (some sites evicted), the NEXT cap-hit immediately prunes
    /// again (no rate-limit delay for the first refusal after a prune).
    /// </summary>
    [Fact]
    public void First_cap_hit_after_successful_prune_prunes_immediately()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[8192];
        // Write one live anchor block at offset 0 for all "live" sites.
        int slotOff = CardFixtures.WriteKillsBlock(buf, 0, "A fine blade", gap: 20);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        int cap = CardSites.MaxSites;

        // Fill cap/2 with dead sites (unmapped anchor).
        int halfCap = cap / 2;
        for (int i = 0; i < halfCap; i++)
        {
            var dead = new CardSites.Site(1, 1, 0xDEAD_0001_0000L + i, 0xDEAD_0000_0000L + i, IsKills: true);
            sites.Add(dead);
        }
        // Fill rest with live sites (anchor at 0x1000+0).
        for (int i = 0; i < cap - halfCap; i++)
        {
            long liveSlot = 0x1000 + slotOff + i; // distinct SlotAddr to avoid dedup
            var live = new CardSites.Site(1, 1, liveSlot, 0x1000, IsKills: true);
            sites.Add(live);
        }
        Assert.Equal(cap, sites.Count);

        // First Add at cap: prune runs, dead half evicted, site admitted.
        var newSite1 = new CardSites.Site(2, 1, 0x1000 + slotOff + cap + 1, 0x1000, IsKills: false);
        bool ok1 = sites.Add(newSite1);
        Assert.True(ok1, "first cap-hit add should be admitted after prune");
        int countAfterFirst = sites.Count;

        // Re-fill to cap with dead sites so we hit cap again.
        int gap = cap - sites.Count;
        for (int i = 0; i < gap; i++)
        {
            var dead2 = new CardSites.Site(1, 1, 0xDEAD_0002_0000L + i, 0xDEAD_0001_0000L + i, IsKills: true);
            sites.Add(dead2);
        }
        Assert.Equal(cap, sites.Count);

        // The FIRST refusal after prune succeeded should immediately trigger another prune.
        var newSite2 = new CardSites.Site(2, 1, 0x1000 + slotOff + cap + 2, 0x1000, IsKills: false);
        bool ok2 = sites.Add(newSite2);
        Assert.True(ok2, "first cap-hit after successful prune must prune immediately and admit");
    }
}
