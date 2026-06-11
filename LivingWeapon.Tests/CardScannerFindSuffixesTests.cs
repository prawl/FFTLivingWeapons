using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CardScanner.FindSuffixes: locates weapon names and their adjacent 2-char suffix slots,
/// validating both name and slot to emit hits tied to weapon ids.
/// </summary>
public class CardScannerFindSuffixesTests
{
    [Fact]
    public void FindSuffixes_ascii_finds_name_and_valid_slot()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", ""));
        var pats = new CardPatterns(meta);
        var nameIds = new List<int> { 1 };

        var parts = new List<byte>();
        var nameB = ByteScan.Enc("Sword", 1);
        var slotB = ByteScan.Enc("  ", 1);
        parts.AddRange(nameB);
        parts.AddRange(slotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, 0, buf.Length, pats, nameIds, hits);

        Assert.NotEmpty(hits);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(1, hits[0].Enc);
    }

    [Fact]
    public void FindSuffixes_utf16_finds_name_and_valid_slot()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", ""));
        var pats = new CardPatterns(meta);
        var nameIds = new List<int> { 1 };

        var parts = new List<byte>();
        var nameB = ByteScan.Enc("Sword", 2);
        var slotB = ByteScan.Enc("  ", 2);
        parts.AddRange(nameB);
        parts.AddRange(slotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, 0, buf.Length, pats, nameIds, hits);

        Assert.NotEmpty(hits);
        Assert.Equal(1, hits[0].Id);
        Assert.Equal(2, hits[0].Enc);
    }

    [Fact]
    public void FindSuffixes_skips_id_not_in_nameids()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", ""), (2, "Staff", ""));
        var pats = new CardPatterns(meta);
        var nameIds = new List<int> { 1 };

        var parts = new List<byte>();
        var name1B = ByteScan.Enc("Sword", 1);
        var slot1B = ByteScan.Enc("  ", 1);
        var name2B = ByteScan.Enc("Staff", 1);
        var slot2B = ByteScan.Enc("  ", 1);
        parts.AddRange(name1B);
        parts.AddRange(slot1B);
        parts.AddRange(name2B);
        parts.AddRange(slot2B);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, 0, buf.Length, pats, nameIds, hits);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Id);
    }

    [Fact]
    public void FindSuffixes_name_slot_extends_past_searchable_but_within_buf_still_reported()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", ""));
        var pats = new CardPatterns(meta);
        var nameIds = new List<int> { 1 };

        var parts = new List<byte>();
        var nameB = ByteScan.Enc("Sword", 1);
        var slotB = ByteScan.Enc("  ", 1);

        parts.AddRange(nameB);
        int lookback = 0;
        int searchable = parts.Count;
        parts.AddRange(slotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, lookback, searchable, pats, nameIds, hits);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public void FindSuffixes_invalid_slot_not_reported()
    {
        var meta = CardScannerFixtures.BuildMetaMap((1, "Sword", ""));
        var pats = new CardPatterns(meta);
        var nameIds = new List<int> { 1 };

        var parts = new List<byte>();
        var nameB = ByteScan.Enc("Sword", 1);
        var badSlotB = ByteScan.Enc("XX", 1);
        parts.AddRange(nameB);
        parts.AddRange(badSlotB);

        byte[] buf = parts.ToArray();

        var hits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, 0, buf.Length, pats, nameIds, hits);

        Assert.Empty(hits);
    }
}
