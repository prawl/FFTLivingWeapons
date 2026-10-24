using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace LivingWeapon.Tests;

public class NameTapPolicyTests
{
    private static IReadOnlyList<(int Id, string Name)> Names(params (int, string)[] rows)
        => rows.Select(r => (r.Item1, r.Item2)).ToList();

    private static IReadOnlyDictionary<int, WeaponMeta> Meta(params (int, string?)[] rows)
        => rows.ToDictionary(r => r.Item1, r => new WeaponMeta { Name = r.Item2! });

    [Fact]
    public void Order_sorts_longest_first_then_by_id_and_skips_empty_names()
    {
        var meta = Meta((5, "Axe"), (3, "Longer Name"), (1, "Bow"), (4, ""), (6, null), (2, "Longer Name"));
        var order = NameTapPolicy.Order(meta);
        Assert.Equal(new[] { 2, 3, 1, 5 }, order.Select(o => o.Id).ToArray());
    }

    [Fact]
    public void TryMatch_finds_the_name_and_its_two_char_tail()
    {
        var ordered = Names((7, "Sanguine Sword"));
        Assert.True(NameTapPolicy.TryMatch("Sanguine Sword+3", ordered, out int id, out string tail));
        Assert.Equal(7, id);
        Assert.Equal("+3", tail);
    }

    [Theory]
    [InlineData("Sanguine Sword  ", "  ")]
    [InlineData("Sanguine Sword+ ", "+ ")]
    public void TryMatch_reads_the_baked_room_and_tier_one_tails(string text, string expected)
    {
        Assert.True(NameTapPolicy.TryMatch(text, Names((7, "Sanguine Sword")), out _, out string tail));
        Assert.Equal(expected, tail);
    }

    [Fact]
    public void A_longer_name_wins_over_the_shorter_one_it_contains()
    {
        var ordered = NameTapPolicy.Order(Meta((1, "Sword"), (2, "Sanguine Sword")));
        Assert.True(NameTapPolicy.TryMatch("Sanguine Sword+2", ordered, out int id, out string tail));
        Assert.Equal(2, id);
        Assert.Equal("+2", tail);
    }

    [Fact]
    public void Markup_around_the_name_still_matches()
    {
        Assert.True(NameTapPolicy.TryMatch("<color=80>Sanguine Sword+3</color>", Names((7, "Sanguine Sword")), out int id, out string tail));
        Assert.Equal(7, id);
        Assert.Equal("+3", tail);
    }

    [Fact]
    public void Name_at_the_end_of_the_text_has_an_empty_tail()
    {
        Assert.True(NameTapPolicy.TryMatch("Sanguine Sword", Names((7, "Sanguine Sword")), out _, out string tail));
        Assert.Equal("", tail);
    }

    [Fact]
    public void No_match_reports_false_minus_one_and_empty_tail()
    {
        Assert.False(NameTapPolicy.TryMatch("Main Menu", Names((7, "Sanguine Sword")), out int id, out string tail));
        Assert.Equal(-1, id);
        Assert.Equal("", tail);
    }

    [Fact]
    public void A_token_bound_name_is_a_known_blind_spot()
    {
        Assert.False(NameTapPolicy.TryMatch("<string=lstr0>", Names((7, "Sanguine Sword")), out _, out _));
    }

    [Fact]
    public void Utf16_lane_finds_wide_names_that_the_ascii_reader_cannot()
    {
        var ordered = Names((7, "Sanguine Sword+3"));
        var wide = Encoding.Unicode.GetBytes("Sanguine Sword+3");
        var raw = new byte[] { 1, 2, 3 }.Concat(wide).ToArray();
        Assert.True(NameTapPolicy.TryMatchUtf16(raw, ordered, out int id, out int offset, out string tail));
        Assert.Equal(7, id);
        Assert.Equal(3, offset);
        Assert.Equal("", tail);

        var withTail = new byte[] { 1, 2, 3 }.Concat(Encoding.Unicode.GetBytes("Sanguine Sword+3")).ToArray();
        var ordered2 = Names((7, "Sanguine Sword"));
        Assert.True(NameTapPolicy.TryMatchUtf16(withTail, ordered2, out _, out int off2, out string tail2));
        Assert.Equal(3, off2);
        Assert.Equal("+3", tail2);
        var nulled = withTail.Take(3 + 28).Concat(new byte[] { 0, 0, 0x41, 0 }).ToArray();
        Assert.True(NameTapPolicy.TryMatchUtf16(nulled, ordered2, out _, out _, out string nulTail));
        Assert.Equal("", nulTail);
        Assert.True(NameTapPolicy.TryMatchUtf16(withTail.Take(3 + 28 + 2).ToArray(), ordered2, out _, out _, out string cut));
        Assert.Equal("+", cut);

        var narrow = Encoding.ASCII.GetBytes("Sanguine Sword+3");
        Assert.False(NameTapPolicy.TryMatchUtf16(narrow, ordered, out int none, out int noOff, out string noTail));
        Assert.Equal(-1, none);
        Assert.Equal(-1, noOff);
        Assert.Equal("", noTail);

        string? asAscii = IconCacheHealPolicy.NameFrom(wide, null);
        Assert.False(asAscii != null && NameTapPolicy.TryMatch(asAscii, ordered, out _, out _));
    }

    [Fact]
    public void Clip_keeps_short_text_and_cuts_long_text_to_sixty_chars()
    {
        Assert.Equal("short", NameTapPolicy.Clip("short"));
        Assert.Equal(60, NameTapPolicy.Clip(new string('x', 200)).Length);
    }

    [Theory]
    [InlineData("<string=lstr0>", true)]
    [InlineData("Equip <string=lstr1> now", true)]
    [InlineData("Sanguine Sword+3", false)]
    [InlineData("", false)]
    public void IsTokenHead_flags_text_that_carries_a_string_token(string text, bool expected)
        => Assert.Equal(expected, NameTapPolicy.IsTokenHead(text));
}
