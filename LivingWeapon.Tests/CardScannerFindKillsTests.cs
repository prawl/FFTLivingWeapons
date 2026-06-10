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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "A sharp blade"), (2, "Staff", "B holy relic"));
        var pats = new CardPatterns(meta);
        byte[] buf = CardScannerTestBase.BuildTwoCards("Sword", "A sharp blade", "Staff", "B holy relic", 1);

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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "A sharp blade"), (2, "Staff", "B holy relic"));
        var pats = new CardPatterns(meta);
        byte[] buf = CardScannerTestBase.BuildTwoCards("Sword", "A sharp blade", "Staff", "B holy relic", 2);

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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "A sharp blade"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc("0   ", 1);
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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        var fillerB = ByteScan.Enc(" stuff\n\n", 1);
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc("0   ", 1);

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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc("0   ", 1);

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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        parts.AddRange(flavorB);
        int lookback = parts.Count;
        var paddingB = ByteScan.Enc("XXXXXXXX", 1);
        parts.AddRange(paddingB);
        int searchable = paddingB.Length;
        var killsB = ByteScan.Enc("Kills: ", 1);
        var killsSlotB = ByteScan.Enc("0   ", 1);
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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        var flavorB = ByteScan.Enc("Sharp", 1);
        var killsB = ByteScan.Enc("Kills: ", 1);
        var badSlotB = ByteScan.Enc("    ", 1);

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
        var meta = CardScannerTestBase.BuildMetaMap((1, "Sword", "Sharp"));
        var pats = new CardPatterns(meta);

        foreach (var slot in new[] { "0   ", "42  ", "137 ", "1337" })
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
}
