using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-257 commit 1: the anchor-verify leniency policy (CardSites.Verify.cs) and the per-pass
/// CardVerdict ledger. THE bug this file pins: pre-fix, CardSites.cs:184 turned ANY anchor-verify
/// failure -- a genuine buffer-reuse mismatch or a one-tick unreadable memory glitch alike --
/// into an eviction on the very first occurrence, silently dropping the card's own painted copy
/// out of the cache while Display.PoolPaint.cs's per-id coverage check stayed satisfied (a
/// weapon keeps ~5.8 pool copies on average, CardSites.cs's own MaxSites sizing comment, so
/// losing one is invisible to a presence-only check).
/// </summary>
public class CardSitesVerifyTests
{
    [Fact]
    public void An_unreadable_anchor_does_not_evict_on_the_first_miss()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int anchorPos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);
        Assert.Equal(1, sites.Count);

        // Simulate a transient unreadable window: the region backing this site's anchor vanishes
        // for one pass (a save-load boundary, a heap generation edge -- the mechanism
        // docs/LIVE_LEDGER.md's flavor-line-overwrite-displays row proves this cache guards against).
        heap.RemoveRegion(0x1000L);

        int writes = sites.PaintAll(id => 42);
        Assert.Equal(0, writes);
        Assert.Equal(1, sites.Count);   // must survive a SINGLE unreadable miss

        // The transient condition resolves: the region is back, byte-identical to before.
        heap.AddRegion(0x1000L, buf, writable: true);

        int writes2 = sites.PaintAll(id => 42);
        Assert.Equal(1, writes2);
        Assert.Equal(1, sites.Count);

        bool ok = heap.TryReadBytes(0x1000 + slotAddr, Signatures.KillsMeterSlotChars, out var painted);
        Assert.True(ok);
        Assert.Equal(ByteScan.Ascii(Signatures.KillsMeterSlot(42)), painted);
    }

    [Fact]
    public void Three_consecutive_unreadable_misses_evict()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int anchorPos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);

        heap.RemoveRegion(0x1000L);

        sites.PaintAll(id => 42);   // strike 1
        Assert.Equal(1, sites.Count);
        sites.PaintAll(id => 42);   // strike 2
        Assert.Equal(1, sites.Count);
        sites.PaintAll(id => 42);   // strike 3 -> the strike counter is not an infinite lease
        Assert.Equal(0, sites.Count);
    }

    [Fact]
    public void A_successful_verify_resets_the_strike_counter()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int anchorPos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);

        heap.RemoveRegion(0x1000L);
        sites.PaintAll(id => 42);   // strike 1
        sites.PaintAll(id => 42);   // strike 2
        Assert.Equal(1, sites.Count);

        heap.AddRegion(0x1000L, buf, writable: true);
        sites.PaintAll(id => 42);   // Live verify -- must reset the count to zero, not just skip incrementing

        heap.RemoveRegion(0x1000L);
        sites.PaintAll(id => 42);   // strike 1 (post-reset)
        sites.PaintAll(id => 42);   // strike 2 (post-reset)
        // If the earlier successful verify had not reset the counter, this pair of misses would
        // carry the OLD count (2) past CardEvictStrikes (3) and the site would already be gone.
        Assert.Equal(1, sites.Count);
    }

    [Fact]
    public void A_mismatched_anchor_still_evicts_on_the_first_miss()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int anchorPos = 10;
        int slotAddr = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20);

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotAddr, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);

        // The region stays fully readable, but the flavor text underneath the anchor is now
        // something else entirely -- a genuine buffer-reuse mismatch (LW-163's contract target),
        // never a transient condition, so the leniency policy must not apply here at all.
        heap.WriteBytes(0x1000 + anchorPos, ByteScan.Ascii("XXXXXXXXXXXX"));

        sites.PaintAll(id => 42);
        Assert.Equal(0, sites.Count);   // evicted on the FIRST mismatch, zero strikes tolerated
    }

    /// <summary>Pins the precedence decision documented on CardSites.Verify.cs's VerifyAnchor: a
    /// readable-but-WRONG flavor anchor evicts immediately, even though the "Kills: " literal
    /// read that a stricter "read failure always wins" rule would also want to consult is never
    /// attempted at all (the short-circuit) -- and, in this fixture, would have come back
    /// Unreadable if it HAD been attempted (SlotAddr is deliberately unmapped). A readable-but-
    /// wrong anchor is strong positive evidence of buffer reuse, so waiting out a strike window
    /// to learn the untried half might have been unreadable would only delay a correct eviction.</summary>
    [Fact]
    public void A_readable_but_wrong_flavor_evicts_even_though_the_kills_literal_would_have_been_unreadable()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        int anchorPos = 10;
        var buf = new byte[64];
        Array.Copy(ByteScan.Ascii("XXXXXXXXXXXX"), 0, buf, anchorPos, 12);   // wrong flavor text, but READABLE

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        // SlotAddr sits far outside the mapped region: the "Kills: " literal read (SlotAddr - 7)
        // would fail if VerifyAnchor ever attempted it.
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + 10_000, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);

        sites.PaintAll(id => 42);

        Assert.Equal(0, sites.Count);   // Mismatch wins on the FIRST pass -- no strikes, no leniency
    }

    [Fact]
    public void A_slot_already_holding_the_desired_text_records_agreed_at_its_address()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        int anchorPos = 10;
        int slotPos = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20, slot: Signatures.KillsMeterSlot(42));

        var heap = new FakeHeap((0x1000L, buf, writable: true));
        var sites = new CardSites(heap, pats);

        long slotAddr = 0x1000 + slotPos;
        var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: slotAddr, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
        sites.Add(site);

        var verdict = new CardVerdict();
        int writes = sites.PaintAll(id => 42, verdict);

        Assert.Equal(0, writes);   // skip-if-equal: the slot already says 42, no write issued
        // LW-257 two-lane restructuring: AlreadyEqual is settled-lane only, never the bounded
        // notable list (CardVerdict's class doc) -- a steady-state pass is nearly all
        // AlreadyEqual, so keeping it out of Entries is what keeps that cap meaningful.
        Assert.Empty(verdict.Entries);
        Assert.True(verdict.Settled(1, addr => addr == slotAddr),
            "the settled lane must carry AlreadyEqual at the site's OWN address, not aggregated by id");
        Assert.False(verdict.Settled(1, addr => addr == slotAddr + 1),
            "the address predicate must actually gate the answer, not be ignored");
    }

    [Fact]
    public void Read_fail_shape_refusal_and_not_writable_are_three_distinct_outcomes()
    {
        var meta = CardSitesFixtures.BuildMeta();
        var pats = new CardPatterns(meta);

        // (a) SlotUnreadable: the anchor and the "Kills: " literal both verify fine, but the
        // region is truncated so the 11-char meter slot itself does not fully fit.
        PaintOutcome unreadable;
        {
            var full = new byte[200];
            int anchorPos = 10;
            int slotPos = CardFixtures.WriteKillsBlock(full, anchorPos, "A fine blade", gap: 20);
            var truncated = new byte[slotPos + 5];   // only 5 of the 11 slot chars fit
            Array.Copy(full, 0, truncated, 0, truncated.Length);

            var heap = new FakeHeap((0x1000L, truncated, writable: true));
            var sites = new CardSites(heap, pats);
            var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotPos, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
            sites.Add(site);

            var verdict = new CardVerdict();
            sites.PaintAll(id => 42, verdict);
            unreadable = Assert.Single(verdict.Entries).Outcome;
            Assert.Equal(PaintOutcome.SlotUnreadable, unreadable);
        }

        // (b) SlotShapeRefused: the slot is fully readable, but its bytes are not a valid meter
        // body at all (no leading digit) -- a foreign write must never be attempted over it.
        PaintOutcome shapeRefused;
        {
            var buf = new byte[200];
            int anchorPos = 10;
            int slotPos = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20, slot: new string('X', Signatures.KillsMeterSlotChars));

            var heap = new FakeHeap((0x1000L, buf, writable: true));
            var sites = new CardSites(heap, pats);
            var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotPos, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
            sites.Add(site);

            var verdict = new CardVerdict();
            sites.PaintAll(id => 42, verdict);
            shapeRefused = Assert.Single(verdict.Entries).Outcome;
            Assert.Equal(PaintOutcome.SlotShapeRefused, shapeRefused);
        }

        // (c) NotWritable: the slot is fully readable, correctly shaped, and DIFFERENT from the
        // desired value (so a write is actually attempted), but the backing region is read-only.
        PaintOutcome notWritable;
        {
            var buf = new byte[200];
            int anchorPos = 10;
            int slotPos = CardFixtures.WriteKillsBlock(buf, anchorPos, "A fine blade", gap: 20, slot: Signatures.KillsMeterSlot(5));

            var heap = new FakeHeap((0x1000L, buf, writable: false));
            var sites = new CardSites(heap, pats);
            var site = new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000 + slotPos, AnchorAddr: 0x1000 + anchorPos, IsKills: true);
            sites.Add(site);

            var verdict = new CardVerdict();
            sites.PaintAll(id => 42, verdict);
            notWritable = Assert.Single(verdict.Entries).Outcome;
            Assert.Equal(PaintOutcome.NotWritable, notWritable);
        }

        // Non-vacuity: the three outcomes must be pairwise distinct, not just individually equal
        // to the value each block already asserted above.
        Assert.NotEqual(unreadable, shapeRefused);
        Assert.NotEqual(shapeRefused, notWritable);
        Assert.NotEqual(unreadable, notWritable);
    }
}
