using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-262: suffix and kills sites are partitioned so a suffix flood can never starve kills
/// coverage. Companion to CardSitesCapReliefTests.cs (the pre-existing shared-cap prune tests,
/// which stay green unmodified -- kills-only Add still goes through the exact same MaxSites/
/// PruneEveryRefusals/PruneDeadSites path).
/// </summary>
public class CardSitesSuffixPartitionTests
{
    private static (CardPatterns pats, FakeHeap heap) Fixture()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);
        var heap = new FakeHeap((0x1000L, new byte[16], writable: true));
        return (pats, heap);
    }

    // ─── Test 1: suffix flood can never starve kills coverage ────────────────

    [Fact]
    public void Suffix_flood_can_never_starve_kills_coverage()
    {
        var (pats, heap) = Fixture();
        var sites = new CardSites(heap, pats);

        int admitted = 0, refused = 0;
        int flood = CardSites.MaxSuffixSites + 500;
        for (int i = 0; i < flood; i++)
        {
            // Spread across many distinct ids (well under SuffixCopiesPerId per id) so the
            // GLOBAL suffix cap is what this loop exercises, not the per-id one (that is test 2).
            int id = 5000 + (i % 200);
            var s = new CardSites.Site(id, i % 2, 0x2000 + i, 0x2000 + i, IsKills: false);
            if (sites.Add(s)) admitted++; else refused++;
        }

        // Non-vacuous per the coordinator's spec: pin the EXACT admitted count, not just "some
        // limit exists" -- a test that only checked refused>0 would still pass if the cap were
        // e.g. 2000 instead of 1024.
        Assert.Equal(CardSites.MaxSuffixSites, admitted);
        Assert.True(refused > 0, "flooding past MaxSuffixSites must produce at least one refusal");

        // The actual guarantee: every tracked id still gets its kills site admitted despite the
        // flood having already consumed the (formerly shared) cache headroom.
        foreach (int id in CardSitesFixtures.BuildMeta().Keys)
        {
            var killSite = new CardSites.Site(id, 1, 0x3000 + id, 0x3000 + id, IsKills: true);
            Assert.True(sites.Add(killSite), $"kills add for id {id} must succeed despite the suffix flood");
        }
    }

    // ─── Test 2: boundary + per-id cap + capacity returned on evict ──────────

    [Fact]
    public void Suffix_boundary_admits_exactly_MaxSuffixSites_then_refuses()
    {
        var (pats, heap) = Fixture();
        var sites = new CardSites(heap, pats);

        for (int i = 0; i < CardSites.MaxSuffixSites; i++)
        {
            int id = 6000 + (i % 200);
            var s = new CardSites.Site(id, i % 2, 0x4000 + i, 0x4000 + i, IsKills: false);
            Assert.True(sites.Add(s), $"suffix add {i} within the cap must be admitted");
        }

        var overflow = new CardSites.Site(6999, 1, 0x9000, 0x9000, IsKills: false);
        Assert.False(sites.Add(overflow), "the (MaxSuffixSites+1)th suffix add must be refused");
    }

    [Fact]
    public void Suffix_per_id_cap_refuses_the_13th_copy_even_with_global_headroom()
    {
        var (pats, heap) = Fixture();
        var sites = new CardSites(heap, pats);

        int id = 42;
        for (int i = 0; i < CardSites.SuffixCopiesPerId; i++)
        {
            // Alternate encodings so both encodings pool against the SAME per-id cap
            // (spec B: "both encodings pooled per id").
            var s = new CardSites.Site(id, i % 2, 0x5000 + i, 0x5000 + i, IsKills: false);
            Assert.True(sites.Add(s), $"per-id copy {i} within SuffixCopiesPerId must be admitted");
        }

        var extra = new CardSites.Site(id, 1, 0x5999, 0x5999, IsKills: false);
        Assert.False(sites.Add(extra), "the 13th copy for the same id must be refused despite global headroom");
    }

    [Fact]
    public void Evicting_a_suffix_site_returns_capacity_for_a_new_suffix_add()
    {
        var (pats, heap) = Fixture();
        var sites = new CardSites(heap, pats);

        for (int i = 0; i < CardSites.MaxSuffixSites; i++)
        {
            int id = 7000 + (i % 200);
            var s = new CardSites.Site(id, i % 2, 0x6000 + i, 0x6000 + i, IsKills: false);
            sites.Add(s);
        }
        // Every filled site's anchor points outside FakeHeap's mapped region (0x1000..0x1010),
        // so it is dead by construction. Evict via PaintAll's own strike path (CardSites.
        // Verify.cs) rather than the policy-refusal path this file's test 3 pins as prune-free.
        var killsFor = new System.Func<int, int>(_ => 0);
        for (int strike = 0; strike < Tuning.CardEvictStrikes; strike++)
            sites.PaintAll(killsFor);

        // Nothing above actually goes dead (every anchor in the fill loop is unmapped-adjacent
        // by construction -- FakeHeap only maps 0x1000..0x1010), so PaintAll's own anchor verify
        // evicts the WHOLE fill after CardEvictStrikes beats. That frees the entire suffix cache.
        var fresh = new CardSites.Site(8000, 1, 0x7000, 0x7000, IsKills: false);
        Assert.True(sites.Add(fresh), "capacity freed by eviction must be available to a new suffix add");
    }

    // ─── LW-269: evicting one suffix copy frees exactly one per-id slot ──────

    /// <summary>LW-269: pins the per-id decrement half of EvictList (CardSites.Admission.cs)
    /// directly -- Evicting_a_suffix_site_returns_capacity_for_a_new_suffix_add above proves
    /// capacity comes back at all, but evicts the WHOLE fill in one PaintAll storm, so it can
    /// never tell "decremented by exactly one" apart from "reset to zero" or "decremented by
    /// some other amount". This test evicts exactly ONE of twelve copies for the SAME id and
    /// checks the per-id count lands exactly one lower, not merely lower.</summary>
    [Fact]
    public void Evicting_one_suffix_copy_frees_exactly_one_per_id_slot()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        // The live anchor for id 1 enc 1 ("Sword") at 0x1000; 0x1100 stays zeroed -- a
        // readable-but-WRONG anchor (Mismatch), which evicts on its very first PaintAll pass
        // (CardSites.Verify.cs's VerifyAnchor/ApplyStrike, LW-163's unweakened contract).
        var buf = new byte[512];
        byte[] liveAnchor = ByteScan.Ascii("Sword");
        Array.Copy(liveAnchor, 0, buf, 0, liveAnchor.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        // 11 keepers: live anchor (survive verify), but an unmapped SlotAddr so their own slot
        // read refuses harmlessly on paint -- they neither write nor get evicted.
        for (int i = 0; i < CardSites.SuffixCopiesPerId - 1; i++)
        {
            var keeper = new CardSites.Site(1, 1, 0xBEEF_0000_0000L + i, 0x1000L, IsKills: false);
            Assert.True(sites.Add(keeper), $"keeper copy {i} within SuffixCopiesPerId must be admitted");
        }

        // The 12th (and last) copy for id 1: a mismatching anchor, so PaintAll evicts it.
        var victim = new CardSites.Site(1, 1, 0xBEEF_0000_1000L, 0x1100L, IsKills: false);
        Assert.True(sites.Add(victim), "the 12th copy (victim) must be admitted");

        // Per-id cap already reached: a 13th add is refused despite the huge global headroom
        // (MaxSuffixSites is 1024), so this refusal is unambiguously the PER-ID cap, not the
        // global one.
        var overflow = new CardSites.Site(1, 1, 0xBEEF_0000_2000L, 0x1000L, IsKills: false);
        Assert.False(sites.Add(overflow), "the 13th copy must be refused before any eviction");

        sites.PaintAll(_ => 0);   // the 11 keepers verify Live and stay; the victim's anchor
                                  // reads back zeros (Mismatch) and is evicted on this one pass.
        Assert.Equal(11, sites.SuffixCount);

        // Evicting the victim must free exactly one per-id slot: a fresh add for the same id
        // is admitted again...
        var replacement = new CardSites.Site(1, 1, 0xBEEF_0000_3000L, 0x1000L, IsKills: false);
        Assert.True(sites.Add(replacement), "evicting one copy must free exactly one per-id slot");

        // ...but only once: the per-id count is back at its cap (12), proving the decrement
        // was exactly one, not a reset of the whole id's count to zero.
        var second = new CardSites.Site(1, 1, 0xBEEF_0000_4000L, 0x1000L, IsKills: false);
        Assert.False(sites.Add(second), "the per-id count must be back at its cap, proving the decrement was one, not a reset");
    }

    // ─── Test 3: a policy refusal must never prune ────────────────────────────

    [Fact]
    public void Policy_refused_suffix_add_never_triggers_prune()
    {
        var (pats, heap) = Fixture();
        var sites = new CardSites(heap, pats);

        // Plant dead KILLS sites up near cap-adjacent territory is irrelevant to a SUFFIX policy
        // refusal (the two caps are independent, spec A) -- what must never move is _refusalsAtCap
        // and the planted dead sites, both diagnostic of "did a prune run".
        // Spread across many distinct ids (well under SuffixCopiesPerId per id, this file's test 1
        // convention) so it is the GLOBAL suffix cap being saturated, not any one id's per-id cap.
        const int deadCount = 50;
        for (int i = 0; i < deadCount; i++)
        {
            int id = 4000 + (i % 200);
            var dead = new CardSites.Site(id, 1, 0xDEAD_2000_0000L + i, 0xDEAD_0000_0000L + i, IsKills: false);
            sites.Add(dead);
        }
        int refusalsBefore = sites.RefusalsAtCapForTest;

        // Saturate the suffix cap with more dead entries so the NEXT suffix add is a genuine
        // policy refusal (cap already full), not a dedup/admit.
        for (int i = deadCount; i < CardSites.MaxSuffixSites; i++)
        {
            int id = 4000 + (i % 200);
            var dead = new CardSites.Site(id, 1, 0xDEAD_2000_0000L + i, 0xDEAD_0000_0000L + i, IsKills: false);
            sites.Add(dead);
        }
        Assert.Equal(CardSites.MaxSuffixSites, sites.Count);

        var refused = new CardSites.Site(9000, 1, 0x1000, 0x1000, IsKills: false);
        Assert.False(sites.Add(refused), "cap-saturated suffix add must be refused");

        Assert.Equal(CardSites.MaxSuffixSites, sites.Count); // nothing evicted
        Assert.Equal(refusalsBefore, sites.RefusalsAtCapForTest); // _refusalsAtCap untouched
    }

    // ─── F5(a): Clear() must reset the new suffix accounting too ─────────────

    /// <summary>Verifier F5(a): Display.Invalidate() calls _sites.Clear() on EVERY battle edge
    /// (Display.cs), so a regression here would permanently wedge suffix painting after the very
    /// first battle with no error anywhere -- a scary-if-regressed behavior that had zero direct
    /// cover (CardSites.cs's own Clear() test, Clear_empties_the_cache in CardSitesCacheTests.cs,
    /// predates the suffix counters entirely and only checks Count/kills-admission).</summary>
    [Fact]
    public void Clear_resets_suffix_accounting_and_admission_works_again()
    {
        var (pats, heap) = Fixture();
        var sites = new CardSites(heap, pats);

        for (int i = 0; i < CardSites.MaxSuffixSites; i++)
        {
            int id = 1000 + (i % 200);
            sites.Add(new CardSites.Site(id, i % 2, 0x8000 + i, 0x8000 + i, IsKills: false));
        }
        Assert.Equal(CardSites.MaxSuffixSites, sites.Count);
        var refusedBeforeClear = new CardSites.Site(9999, 1, 0x1, 0x1, IsKills: false);
        Assert.False(sites.Add(refusedBeforeClear), "the suffix cache must be genuinely full before Clear");

        sites.Clear();
        Assert.Equal(0, sites.Count);
        Assert.Equal(0, sites.SuffixCount);

        // The same id that was at its OWN per-id cap before Clear must be admittable again too,
        // not just the global count.
        var afterClear = new CardSites.Site(1000, 1, 0x2, 0x2, IsKills: false);
        Assert.True(sites.Add(afterClear), "suffix admission must work again after Clear resets the accounting");
    }

    // ─── Test 4: a low-yield prune does not re-arm immediate mode ────────────

    [Fact]
    public void Prune_evicting_below_floor_does_not_rearm()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        // T2's own fixture shape (CardSitesCapReliefTests.Cap_full_of_live_sites_refused_and_
        // prune_is_rate_limited), NOT T3's: VerifyAnchor for a kills site checks the "Kills: "
        // literal at SlotAddr - 7 (CardSites.Verify.cs's VerifyAnchor), so every genuinely-live
        // site here must share ONE constant SlotAddr (pointing at the one real "Kills: " + slot
        // block written below) with a DISTINCT AnchorAddr each (its own written flavor copy) --
        // varying SlotAddr per site instead (T3's pattern) only verifies live for whichever site's
        // SlotAddr happens to coincide with the real block, which is why this test needs the
        // correct pattern to make a PRECISE eviction-count assertion (T3 never asserts one).
        int cap = CardSites.MaxSites;
        int anchorStride = 20;
        int bufSize = cap * anchorStride + 512;
        var buf = new byte[bufSize];

        byte[] flavorBytes = ByteScan.Ascii("A fine blade");
        byte[] prefixBytes = ByteScan.Ascii("Kills: ");
        byte[] slotBytes   = ByteScan.Ascii("0   ");

        int extraBase = cap * anchorStride;
        for (int i = 0; i < cap; i++)
            Array.Copy(flavorBytes, 0, buf, i * anchorStride, flavorBytes.Length);
        Array.Copy(prefixBytes, 0, buf, extraBase, prefixBytes.Length);
        Array.Copy(slotBytes, 0, buf, extraBase + prefixBytes.Length, slotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);
        long liveSlotAddr = 0x1000 + extraBase + prefixBytes.Length;

        const int fewDead = CardSites.PruneRearmFloor - 1; // below the re-arm floor

        for (int i = 0; i < fewDead; i++)
        {
            var dead = new CardSites.Site(1, 1, 0xDEAD_3000_0000L + i, 0xDEAD_0000_0000L + i, IsKills: true);
            sites.Add(dead);
        }
        for (int i = fewDead; i < cap; i++)
        {
            long anchor = 0x1000 + i * anchorStride;
            var live = new CardSites.Site(1, 1, liveSlotAddr, anchor, IsKills: true);
            sites.Add(live);
        }
        Assert.Equal(cap, sites.Count);

        // First cap-hit: _pruneImmediately starts true (fresh CardSites), so this one prunes and
        // evicts exactly `fewDead` (< PruneRearmFloor) sites, then admits.
        var first = new CardSites.Site(3, 1, 0xABCD_0000_0000L, 0xABCD_1000_0000L, IsKills: true);
        Assert.True(sites.Add(first));
        Assert.Equal(cap - fewDead + 1, sites.Count);

        // Re-fill to cap with sites that would ALSO be dead if pruned (so a second prune, if it
        // ran, would be observable via a Count drop).
        int gap = cap - sites.Count;
        for (int i = 0; i < gap; i++)
        {
            var dead2 = new CardSites.Site(1, 1, 0xDEAD_4000_0000L + i, 0xDEAD_0001_0000L + i, IsKills: true);
            sites.Add(dead2);
        }
        Assert.Equal(cap, sites.Count);

        // The prune that just ran evicted fewer than PruneRearmFloor sites, so _pruneImmediately
        // must NOT have re-armed: this next cap-hit is refused outright (rate-limited), not pruned.
        var second = new CardSites.Site(3, 1, 0xABCD_0000_0001L, 0xABCD_1000_0001L, IsKills: true);
        bool admitted = sites.Add(second);
        Assert.False(admitted, "a low-yield prune must not re-arm immediate retry");
        Assert.Equal(cap, sites.Count); // nothing evicted by this refused attempt
    }

    // ─── Test 5 (LW-269): the floor is a re-arm boundary, not just a below-floor guard ──

    /// <summary>LW-269: the sibling test above pins PruneRearmFloor - 1 (does not re-arm); this
    /// pins PruneRearmFloor ITSELF (does re-arm), the boundary the sibling never touches. Copies
    /// that test's T2 fixture shape verbatim (its own comment explains why: a kills-site anchor
    /// verify keys off ONE shared SlotAddr with a distinct AnchorAddr per site).</summary>
    [Fact]
    public void Prune_evicting_exactly_the_floor_rearms_immediate_retry()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        int cap = CardSites.MaxSites;
        int anchorStride = 20;
        int bufSize = cap * anchorStride + 512;
        var buf = new byte[bufSize];

        byte[] flavorBytes = ByteScan.Ascii("A fine blade");
        byte[] prefixBytes = ByteScan.Ascii("Kills: ");
        byte[] slotBytes   = ByteScan.Ascii("0   ");

        int extraBase = cap * anchorStride;
        for (int i = 0; i < cap; i++)
            Array.Copy(flavorBytes, 0, buf, i * anchorStride, flavorBytes.Length);
        Array.Copy(prefixBytes, 0, buf, extraBase, prefixBytes.Length);
        Array.Copy(slotBytes, 0, buf, extraBase + prefixBytes.Length, slotBytes.Length);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);
        long liveSlotAddr = 0x1000 + extraBase + prefixBytes.Length;

        const int floorDead = CardSites.PruneRearmFloor; // ON the re-arm floor, not below it

        for (int i = 0; i < floorDead; i++)
        {
            var dead = new CardSites.Site(1, 1, 0xDEAD_3000_0000L + i, 0xDEAD_0000_0000L + i, IsKills: true);
            sites.Add(dead);
        }
        for (int i = floorDead; i < cap; i++)
        {
            long anchor = 0x1000 + i * anchorStride;
            var live = new CardSites.Site(1, 1, liveSlotAddr, anchor, IsKills: true);
            sites.Add(live);
        }
        Assert.Equal(cap, sites.Count);

        // First cap-hit: _pruneImmediately starts true (fresh CardSites), so this one prunes and
        // evicts exactly `floorDead` (== PruneRearmFloor) sites, then admits. Meeting the floor
        // exactly re-arms _pruneImmediately (CardSites.Admission.cs's PruneDeadSites: the rearm
        // check is `>=`, so the boundary value itself counts as "met").
        var first = new CardSites.Site(3, 1, 0xABCD_0000_0000L, 0xABCD_1000_0000L, IsKills: true);
        Assert.True(sites.Add(first));
        Assert.Equal(cap - floorDead + 1, sites.Count);

        // Re-fill to cap with sites that would ALSO be dead if pruned (so a second prune, if it
        // ran, would be observable via a Count drop).
        int gap = cap - sites.Count;
        for (int i = 0; i < gap; i++)
        {
            var dead2 = new CardSites.Site(1, 1, 0xDEAD_4000_0000L + i, 0xDEAD_0001_0000L + i, IsKills: true);
            sites.Add(dead2);
        }
        Assert.Equal(cap, sites.Count);

        // The prune that just ran evicted exactly the floor, so _pruneImmediately DID re-arm:
        // this next cap-hit prunes immediately (unlike the sibling test, which is refused
        // outright with no second prune at all) and admits in the same call.
        //
        // DEVIATION FROM THE ORIGINAL SPEC ARITHMETIC (flagged per the task instructions): the
        // spec predicted the final count as `cap - gap + 1`, reasoning that the second prune
        // evicts only the `gap` dead2 refill sites just added. That undercounts by one: `first`
        // (id 3) is itself untracked in `meta` (only ids 1 and 2 exist), so CardPatterns.TryGet
        // fails for it and VerifyAnchor returns Mismatch regardless of its address -- `first` is
        // dead too, and PruneDeadSites evicts it right alongside the `gap` dead2 sites on this
        // second pass (gap + 1 == floorDead evictions, again meeting the floor exactly). The
        // sibling (below-floor) test never exposes this because its second cap-hit never runs a
        // real prune at all (rate-limited refusal). The correct steady-state count after any
        // prune-then-admit cycle that evicts exactly `floorDead` sites is `cap - floorDead + 1`,
        // the SAME value as after the first admit -- confirmed by direct trace, not assumption.
        var second = new CardSites.Site(3, 1, 0xABCD_0000_0001L, 0xABCD_1000_0001L, IsKills: true);
        bool admitted = sites.Add(second);
        Assert.True(admitted, "a prune that evicts exactly the floor must re-arm immediate retry");
        Assert.Equal(cap - floorDead + 1, sites.Count);
    }
}
