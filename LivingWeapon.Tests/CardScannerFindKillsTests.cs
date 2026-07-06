using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardScanner.FindKills: ties each "Kills: " to the NEAREST preceding flavor within
/// FlavorWindow, searched across ALL weapons in the patterns, so different weapons' kills
/// are never crossed (regression against the old "same-encoded-value" bug where B's
/// "Kills: " slot could be tied to A's flavor if they were contiguous).
/// </summary>
public class CardScannerFindKillsTests
{
    [Fact]
    public void FindKills_regression_two_contiguous_cards_ascii_ties_each_to_its_own_flavor()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "A sharp blade"), (2, "Staff", "B holy relic"));
        var pats = new CardPatterns(meta);
        byte[] buf = CardScannerFixtures.BuildTwoCards("Sword", "A sharp blade", "Staff", "B holy relic", 1);

        var hits = new List<CardScanner.KillsHit>();
        int lookback = 0;
        int searchable = buf.Length;
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        Assert.Equal(2, hits.Count);
        Assert.NotEqual(hits[0].Id, hits[1].Id);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);
    }

    [Fact]
    public void FindKills_regression_two_contiguous_cards_utf16_ties_each_to_its_own_flavor()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "A sharp blade"), (2, "Staff", "B holy relic"));
        var pats = new CardPatterns(meta);
        byte[] buf = CardScannerFixtures.BuildTwoCards("Sword", "A sharp blade", "Staff", "B holy relic", 2);

        var hits = new List<CardScanner.KillsHit>();
        int lookback = 0;
        int searchable = buf.Length;
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);
    }

    [Fact]
    public void FindKills_no_flavor_in_window_drops_the_hit()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "A sharp blade"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc(Signatures.KillsMeterSlot(0), 1);
        parts.AddRange(killsB);
        parts.AddRange(killsSlotB);
        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.KillsHit>();
        int lookback = 0;
        int searchable = buf.Length;
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        Assert.Empty(hits);
    }

    [Fact]
    public void FindKills_lookback_regression_flavor_entirely_in_lookback_prefix()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        var fillerB = ByteScan.Enc(" stuff\n\n", 1);
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc(Signatures.KillsMeterSlot(0), 1);

        parts.AddRange(flavorB);
        parts.AddRange(fillerB);
        int lookback = parts.Count;
        parts.AddRange(killsB);
        parts.AddRange(killsSlotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.KillsHit>();
        int searchable = buf.Length - lookback;
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        int dist = lookback - (flavorB.Length);
        if (dist <= CardScanner.FlavorWindow)
        {
            Assert.NotEmpty(hits);
            Assert.Equal(1, hits[0].Id);
        }
    }

    [Fact]
    public void FindKills_hit_starting_before_lookback_not_emitted()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc(Signatures.KillsMeterSlot(0), 1);

        parts.AddRange(flavorB);
        parts.AddRange(killsB);
        int hitStart = parts.Count;
        parts.AddRange(killsSlotB);

        byte[] buf = parts.ToArray();

        int lookback = hitStart;
        int searchable = buf.Length - lookback;

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        Assert.Empty(hits);
    }

    [Fact]
    public void FindKills_hit_starting_after_searchable_window_not_emitted()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        parts.AddRange(flavorB);
        int lookback = parts.Count;
        var paddingB = ByteScan.Enc("XXXXXXXX", 1);
        parts.AddRange(paddingB);
        int searchable = paddingB.Length;
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc(Signatures.KillsMeterSlot(0), 1);
        parts.AddRange(killsB);
        parts.AddRange(killsSlotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        Assert.Empty(hits);
    }

    [Fact]
    public void FindKills_slot_validation_rejects_non_digit_first_char()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        var killsB = ByteScan.Enc("Kills: ", 1);
        // Full Signatures.KillsMeterSlotChars width (11 spaces) so this genuinely exercises the
        // digit-first rejection rather than an incidental buffer-too-short bounds reject.
        var badSlotB = ByteScan.Enc("           ", 1);

        parts.AddRange(flavorB);
        parts.AddRange(killsB);
        parts.AddRange(badSlotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, 0, buf.Length, pats, hits);

        Assert.Empty(hits);
    }

    [Fact]
    public void FindKills_slot_validation_accepts_left_aligned_digits()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        foreach (var slot in new[] { "0/5 to +   ", "49/50 to +3", "55         " })
        {
            var parts = new List<byte>();
            var flavorB = ByteScan.Enc("Sharp", 1);
            var killsB = ByteScan.Enc("Kills: ", 1);
            var slotB = ByteScan.Enc(slot, 1);

            parts.AddRange(flavorB);
            parts.AddRange(killsB);
            parts.AddRange(slotB);

            byte[] buf = parts.ToArray();

            var hits = new List<CardScanner.KillsHit>();
            CardScanner.FindKills(buf, 0, buf.Length, pats, hits);

            Assert.NotEmpty(hits);
        }
    }

    // ─── Bidirectional forward attribution (Reliquary equip-meter layout: Kills line FIRST,
    //     flavor below), the KEY DESIGN CHANGE (plan v2). The owner's flavor is never
    //     allocated far from its OWN "Kills: " hit (fixed slot + "\n\n"), whether it sits above
    //     (every pre-existing fixture) or below (the new deployed bake), so the search must try
    //     BOTH directions and pick the nearest. ───

    [Fact]
    public void FindKills_forward_attribution_two_contiguous_new_layout_cards_tie_each_to_its_own_flavor()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "A sharp blade"), (2, "Staff", "B holy relic"));
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        var (killsPosA, flavorPosA) = CardFixtures.WriteCardForward(buf, 0, "A sharp blade");
        int cardBGap = 20; // small gap so a reverted backward-only search would misattribute B's
                            // hit to A's flavor instead of dropping/finding B's own (below) flavor.
        int nextCardStart = flavorPosA + ByteScan.Enc("A sharp blade", 1).Length + cardBGap;
        var (killsPosB, flavorPosB) = CardFixtures.WriteCardForward(buf, nextCardStart, "B holy relic");

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(flavorPosA, hits[0].FlavorPos);
        Assert.Equal(2, hits[1].Id);
        Assert.Equal(flavorPosB, hits[1].FlavorPos);
    }

    [Fact]
    public void FindKills_forward_attribution_single_new_layout_card_ties_to_its_below_flavor()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "A sharp blade"));
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        var (killsPos, flavorPos) = CardFixtures.WriteCardForward(buf, 0, "A sharp blade");

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(flavorPos, hits[0].FlavorPos);
    }

    [Fact]
    public void FindKills_old_backward_layout_two_contiguous_cards_still_resolves_identically()
    {
        // Non-regression: bidirectional must not disturb the backward-only resolution the
        // original (pre-existing) two-contiguous-card regression test locks in.
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", "A sharp blade"), (2, "Staff", "B holy relic"));
        var pats = new CardPatterns(meta);
        byte[] buf = CardScannerFixtures.BuildTwoCards("Sword", "A sharp blade", "Staff", "B holy relic", 1);

        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback: 0, searchable: buf.Length, pats, hits);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[1].Id);
    }
}
