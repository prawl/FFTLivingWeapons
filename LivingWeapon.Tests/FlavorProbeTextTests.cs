using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// FlavorProbeText -- the pure half of the P4 flavor-render probe (docs/RELIQUARY_AC.md P4).
/// The #if LWDEV shell (FlavorSpike.cs) is not visible to the test build (LWDEV is not defined
/// for `dotnet test`), so only the pure static helpers are exercised here.
/// </summary>
public class FlavorProbeTextTests
{
    // Mirrors FlavorProbeText's private base text -- kept here so tests assert against a known
    // literal rather than re-deriving it from the SUT.
    private const string BaseText = "P4 FLAVOR PROBE -- THE BLADE REMEMBERS";

    [Fact]
    public void Compose_pads_to_exact_char_count()
    {
        int n = BaseText.Length + 12;
        string got = FlavorProbeText.Compose(n);

        Assert.Equal(n, got.Length);
        Assert.StartsWith(BaseText, got);
        Assert.Equal(new string(' ', 12), got.Substring(BaseText.Length));
    }

    [Fact]
    public void Compose_truncates_to_exact_char_count()
    {
        int n = 10;
        string got = FlavorProbeText.Compose(n);

        Assert.Equal(n, got.Length);
        Assert.Equal(BaseText.Substring(0, n), got);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Compose_zero_or_negative_returns_empty(int n)
    {
        Assert.Equal("", FlavorProbeText.Compose(n));
    }

    [Fact]
    public void Compose_is_pure_ascii()
    {
        // Longer than BaseText so both the literal and the pad run are covered.
        string got = FlavorProbeText.Compose(BaseText.Length + 20);
        foreach (char c in got)
            Assert.True(c < 0x80, $"non-ASCII char 0x{(int)c:X} in composed line");
    }

    [Fact]
    public void CharCount_enc1_is_byte_len_and_enc2_halves()
    {
        Assert.Equal(37, FlavorProbeText.CharCount(37, 1));
        Assert.Equal(18, FlavorProbeText.CharCount(36, 2));
    }

    [Fact]
    public void TargetWeapon_picks_lowest_id_with_kills_site()
    {
        var sites = new List<CardSites.Site>
        {
            // suffix-only ids BELOW the kills ids -- must be ignored, not picked.
            new CardSites.Site(Id: 1, Enc: 1, SlotAddr: 0x1000, AnchorAddr: 0x1010, IsKills: false),
            new CardSites.Site(Id: 2, Enc: 2, SlotAddr: 0x1100, AnchorAddr: 0x1110, IsKills: false),
            // kills sites -- lowest id (3) must win even though 5's site is added first.
            new CardSites.Site(Id: 5, Enc: 1, SlotAddr: 0x2000, AnchorAddr: 0x2010, IsKills: true),
            new CardSites.Site(Id: 3, Enc: 2, SlotAddr: 0x3000, AnchorAddr: 0x3010, IsKills: true),
            new CardSites.Site(Id: 3, Enc: 1, SlotAddr: 0x3100, AnchorAddr: 0x3110, IsKills: false),
        };

        Assert.Equal(3, FlavorProbeText.TargetWeapon(sites));
    }

    [Fact]
    public void TargetWeapon_empty_returns_zero()
    {
        Assert.Equal(0, FlavorProbeText.TargetWeapon(new List<CardSites.Site>()));
    }

    // THE LOAD-BEARING TEST: guarantees the in-place overwrite can never be mis-sized. enc 2
    // (UTF-16LE) flavor patterns are always even byte length (2 bytes/char, per CardPatterns'
    // Encoding.Unicode bake) so only even L values are realistic for enc 2; enc 1 has no such
    // constraint and is tested with an odd-looking length too.
    [Theory]
    [InlineData(20, 1)]
    [InlineData(37, 1)]
    [InlineData(20, 2)]
    [InlineData(38, 2)]
    public void Encoded_payload_byte_length_matches_flavor_pattern_both_encodings(int flavorByteLen, int enc)
    {
        int charCount = FlavorProbeText.CharCount(flavorByteLen, enc);
        string line = FlavorProbeText.Compose(charCount);
        byte[] payload = ByteScan.Enc(line, enc);

        Assert.Equal(flavorByteLen, payload.Length);
    }
}
