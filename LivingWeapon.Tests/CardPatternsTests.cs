using System;
using System.Collections.Generic;
using System.Text;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Pre-encoded per-weapon search patterns built once at startup.
/// Round-trips both encodings; Kills/Slots match their input sources byte-exact;
/// empty flavor yields an empty array; TryGet correctness; MaxAnchorLen correct.
/// </summary>
public class CardPatternsTests
{
    private static WeaponMeta BuildMeta(int id, string name, string flavor)
    {
        return new WeaponMeta { Name = name, Flavor = flavor };
    }

    private static Dictionary<int, WeaponMeta> BuildMetaMap(params (int, string, string)[] items)
    {
        var map = new Dictionary<int, WeaponMeta>();
        foreach (var (id, name, flavor) in items)
            map[id] = BuildMeta(id, name, flavor);
        return map;
    }

    [Fact]
    public void Builds_entries_for_both_encodings_per_weapon()
    {
        var meta = BuildMetaMap((1, "Sword", "A sharp blade"));
        var patterns = new CardPatterns(meta);
        Assert.True(patterns.TryGet(1, 1, out _), "should have enc=1");
        Assert.True(patterns.TryGet(1, 2, out _), "should have enc=2");
    }

    [Fact]
    public void Skips_weapons_with_empty_name()
    {
        var meta = BuildMetaMap((1, "", "flavor"));
        var patterns = new CardPatterns(meta);
        Assert.False(patterns.TryGet(1, 1, out _), "should skip empty name");
        Assert.False(patterns.TryGet(1, 2, out _), "should skip empty name");
    }

    [Fact]
    public void Includes_weapons_with_empty_flavor()
    {
        var meta = BuildMetaMap((1, "Sword", ""));
        var patterns = new CardPatterns(meta);
        Assert.True(patterns.TryGet(1, 1, out var e1), "should include despite empty flavor");
        Assert.True(patterns.TryGet(1, 2, out var e2), "should include despite empty flavor");
        Assert.Empty(e1.Flavor);
        Assert.Empty(e2.Flavor);
    }

    [Fact]
    public void Rounds_trip_ascii_name_and_flavor()
    {
        const string name = "Sword";
        const string flavor = "A sharp blade";
        var meta = BuildMetaMap((1, name, flavor));
        var patterns = new CardPatterns(meta);

        Assert.True(patterns.TryGet(1, 1, out var e));
        byte[] expectedName = ByteScan.Ascii(name);
        byte[] expectedFlavor = ByteScan.Ascii(flavor);
        Assert.Equal(expectedName, e.Name);
        Assert.Equal(expectedFlavor, e.Flavor);
    }

    [Fact]
    public void Rounds_trip_utf16_name_and_flavor()
    {
        const string name = "Sword";
        const string flavor = "A sharp blade";
        var meta = BuildMetaMap((1, name, flavor));
        var patterns = new CardPatterns(meta);

        Assert.True(patterns.TryGet(1, 2, out var e));
        byte[] expectedName = ByteScan.Utf16(name);
        byte[] expectedFlavor = ByteScan.Utf16(flavor);
        Assert.Equal(expectedName, e.Name);
        Assert.Equal(expectedFlavor, e.Flavor);
    }

    [Fact]
    public void Kills_ascii_matches_literal_bytes()
    {
        var meta = BuildMetaMap((1, "Sword", "flavor"));
        var patterns = new CardPatterns(meta);

        byte[] expected = ByteScan.Ascii("Kills: ");
        byte[] got = patterns.Kills(1);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Kills_utf16_matches_literal_bytes()
    {
        var meta = BuildMetaMap((1, "Sword", "flavor"));
        var patterns = new CardPatterns(meta);

        byte[] expected = ByteScan.Utf16("Kills: ");
        byte[] got = patterns.Kills(2);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Slots_ascii_matches_tuning_suffix_values()
    {
        var meta = BuildMetaMap((1, "Sword", "flavor"));
        var patterns = new CardPatterns(meta);

        var slots = patterns.Slots(1);
        var expected = new List<byte[]>();
        foreach (var s in Tuning.Suffix)
            expected.Add(ByteScan.Ascii(s));

        Assert.Equal(expected.Count, slots.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], slots[i]);
    }

    [Fact]
    public void Slots_utf16_matches_tuning_suffix_values()
    {
        var meta = BuildMetaMap((1, "Sword", "flavor"));
        var patterns = new CardPatterns(meta);

        var slots = patterns.Slots(2);
        var expected = new List<byte[]>();
        foreach (var s in Tuning.Suffix)
            expected.Add(ByteScan.Utf16(s));

        Assert.Equal(expected.Count, slots.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], slots[i]);
    }

    [Fact]
    public void TryGet_returns_false_for_missing_id()
    {
        var meta = BuildMetaMap((1, "Sword", "flavor"));
        var patterns = new CardPatterns(meta);

        Assert.False(patterns.TryGet(999, 1, out _));
        Assert.False(patterns.TryGet(999, 2, out _));
    }

    [Fact]
    public void TryGet_returns_false_for_invalid_encoding()
    {
        var meta = BuildMetaMap((1, "Sword", "flavor"));
        var patterns = new CardPatterns(meta);

        Assert.False(patterns.TryGet(1, 0, out _), "enc=0 invalid");
        Assert.False(patterns.TryGet(1, 3, out _), "enc=3 invalid");
    }

    [Fact]
    public void MaxAnchorLen_is_max_of_all_encoded_byte_lengths()
    {
        var meta = BuildMetaMap(
            (1, "Short", "X"),           // name=5 chars, flavor=1 char
            (2, "Longgername", "Flavor text here"),  // name=12 chars, flavor=16 chars
            (3, "Med", "MediumFlavor")   // name=3 chars, flavor=12 chars
        );
        var patterns = new CardPatterns(meta);

        // The longest string is "Flavor text here" (16 chars).
        // UTF-16LE encodes it as 32 bytes, which is the correct maximum.
        Assert.Equal(32, patterns.MaxAnchorLen);
    }

    [Fact]
    public void MaxAnchorLen_handles_empty_flavor()
    {
        var meta = BuildMetaMap(
            (1, "VeryLongName", "")   // name=12 chars, flavor=0
        );
        var patterns = new CardPatterns(meta);

        // "VeryLongName" (12 chars) in UTF-16LE = 24 bytes.
        Assert.Equal(24, patterns.MaxAnchorLen);
    }

    [Fact]
    public void Entries_returns_all_included_entries()
    {
        var meta = BuildMetaMap(
            (1, "Sword", "flavor1"),
            (2, "", "flavor2"),         // should be skipped
            (3, "Staff", "flavor3")
        );
        var patterns = new CardPatterns(meta);

        // 2 weapons (1,3) x 2 encodings = 4 entries
        Assert.Equal(4, patterns.Entries.Count);

        // Verify we can find them
        Assert.True(patterns.TryGet(1, 1, out _));
        Assert.True(patterns.TryGet(1, 2, out _));
        Assert.True(patterns.TryGet(3, 1, out _));
        Assert.True(patterns.TryGet(3, 2, out _));

        // Verify the skipped one is not there
        Assert.False(patterns.TryGet(2, 1, out _));
    }

    [Fact]
    public void Null_flavor_does_not_throw()
    {
        // B2: Newtonsoft can deserialize a JSON null into Flavor, producing a WeaponMeta
        // whose Flavor property is null. The ctor must not NRE; the entry should have
        // an empty Flavor array.
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 1, new WeaponMeta { Name = "Sword", Flavor = null! } }
        };
        var patterns = new CardPatterns(meta);

        Assert.True(patterns.TryGet(1, 1, out var e1));
        Assert.Empty(e1.Flavor);
    }

    [Fact]
    public void MaxAnchorLen_is_in_bytes_not_chars()
    {
        // B4: MaxAnchorLen is documented as bytes. UTF-16LE encodes each char as 2 bytes,
        // so a 5-char name yields a 10-byte Name array. MaxAnchorLen must report the
        // MAX OVER ENCODED byte lengths (across both encodings), NOT the char count.
        var meta = BuildMetaMap((1, "Sword", "Hi"));   // name=5 chars=10 utf16 bytes; flavor=2 chars=4 utf16 bytes
        var patterns = new CardPatterns(meta);

        // The UTF-16 Name for "Sword" is 10 bytes; that is the correct max.
        Assert.Equal(10, patterns.MaxAnchorLen);
    }

    [Fact]
    public void Handles_non_ascii_characters_in_name()
    {
        // ByteScan.Ascii drops non-ASCII; UTF-16 keeps them
        var meta = BuildMetaMap((1, "Épée", "Flavor"));  // é is non-ASCII
        var patterns = new CardPatterns(meta);

        Assert.True(patterns.TryGet(1, 1, out var e1));
        Assert.True(patterns.TryGet(1, 2, out var e2));

        // ASCII should have dropped the é
        byte[] asciiExpected = ByteScan.Ascii("Épée");
        Assert.Equal(asciiExpected, e1.Name);

        // UTF-16 keeps it
        byte[] utf16Expected = ByteScan.Utf16("Épée");
        Assert.Equal(utf16Expected, e2.Name);
    }
}
