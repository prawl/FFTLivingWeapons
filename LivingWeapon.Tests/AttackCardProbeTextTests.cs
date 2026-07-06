using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCardProbeText, the pure half of the LW-31 Attack-menu census instrument
/// (docs/TODO.md Now section). The #if LWDEV shell (AttackCardSpike.cs) is not visible to the
/// test build (LWDEV is not defined for dotnet test), so only the pure static helpers are
/// exercised here, mirroring HeaderProbeTextTests' arrangement for the LW-27 header spike.
/// </summary>
public class AttackCardProbeTextTests
{
    private const int Cap = 128;

    // THE LOAD-BEARING TEST: the footprint boundary. The write can never exceed the original
    // desc's own byte footprint, so a desc exactly Payload.Length chars long must pass, and one
    // char shorter must fail.
    [Fact]
    public void FitsFootprint_boundary_is_inclusive()
    {
        Assert.True(AttackCardProbeText.FitsFootprint(AttackCardProbeText.Payload.Length));
        Assert.False(AttackCardProbeText.FitsFootprint(AttackCardProbeText.Payload.Length - 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Pattern_and_PayloadBytes_lengths_match_their_char_counts(int enc)
    {
        byte[] pattern = AttackCardProbeText.Pattern(enc);
        byte[] payload = AttackCardProbeText.PayloadBytes(enc);

        Assert.Equal(AttackCardProbeText.Label.Length * enc, pattern.Length);
        Assert.Equal(AttackCardProbeText.Payload.Length * enc, payload.Length);
        Assert.True(payload.Length > 0);   // non-vacuous: both encodings actually produce bytes
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PayloadWithTerminator_adds_one_encoded_NUL_char(int enc)
    {
        byte[] payload = AttackCardProbeText.PayloadBytes(enc);
        byte[] withNul = AttackCardProbeText.PayloadWithTerminator(enc);

        Assert.Equal(payload.Length + enc, withNul.Length);
        for (int i = 0; i < enc; i++)
            Assert.Equal(0, withNul[payload.Length + i]);
    }

    // ---- EncodeWithTerminator / 2-arg FitsFootprint (LW-31 stage 2: promoted for AttackCard.cs's
    // production composer, whose written text varies unlike this file's fixed dev Payload) ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EncodeWithTerminator_matches_PayloadWithTerminator_for_the_dev_payload(int enc)
    {
        // PayloadWithTerminator now delegates to EncodeWithTerminator, and must be byte-identical.
        Assert.Equal(AttackCardProbeText.PayloadWithTerminator(enc), AttackCardProbeText.EncodeWithTerminator(AttackCardProbeText.Payload, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EncodeWithTerminator_encodes_arbitrary_text_plus_one_NUL_char(int enc)
    {
        const string text = "Windrunner Kills: 42.";
        byte[] textBytes = ByteScan.Enc(text, enc);
        byte[] withNul = AttackCardProbeText.EncodeWithTerminator(text, enc);

        Assert.Equal(textBytes.Length + enc, withNul.Length);
        for (int i = 0; i < textBytes.Length; i++) Assert.Equal(textBytes[i], withNul[i]);
        for (int i = 0; i < enc; i++) Assert.Equal(0, withNul[textBytes.Length + i]);
    }

    [Fact]
    public void FitsFootprint_two_arg_boundary_is_inclusive()
    {
        Assert.True(AttackCardProbeText.FitsFootprint(descChars: 42, neededChars: 42));
        Assert.False(AttackCardProbeText.FitsFootprint(descChars: 41, neededChars: 42));
    }

    [Fact]
    public void FitsFootprint_one_arg_overload_still_fixes_neededChars_to_Payload_length()
    {
        Assert.Equal(
            AttackCardProbeText.FitsFootprint(50, AttackCardProbeText.Payload.Length),
            AttackCardProbeText.FitsFootprint(50));
    }

    [Fact]
    public void DescStart_matches_the_label_length_plus_its_own_terminator()
    {
        // enc 1: 6 label bytes + 1 NUL = 7. enc 2: 12 label bytes + 2 NUL bytes = 14.
        Assert.Equal(7, AttackCardProbeText.DescStart(0, 1));
        Assert.Equal(14, AttackCardProbeText.DescStart(0, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_accepts_the_label_mid_buffer(int enc)
    {
        byte[] buf = Concat(Nul(enc), Enc(AttackCardProbeText.Label, enc), Nul(enc));
        int pos = enc;   // right after the leading NUL char

        Assert.True(AttackCardProbeText.IsStandaloneHit(buf, pos, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_accepts_at_buffer_start(int enc)
    {
        byte[] buf = Concat(Enc(AttackCardProbeText.Label, enc), Nul(enc));

        Assert.True(AttackCardProbeText.IsStandaloneHit(buf, 0, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_rejects_a_longer_word_with_a_letter_following(int enc)
    {
        // "Attacks\0": prose use ("Attacks", "attack power"), not the standalone row string.
        byte[] buf = Concat(Nul(enc), Enc(AttackCardProbeText.Label + "s", enc), Nul(enc));
        int pos = enc;

        Assert.False(AttackCardProbeText.IsStandaloneHit(buf, pos, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_rejects_wrong_case(int enc)
    {
        byte[] buf = Concat(Nul(enc), Enc("attack", enc), Nul(enc));
        int pos = enc;

        Assert.False(AttackCardProbeText.IsStandaloneHit(buf, pos, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_rejects_a_hit_not_preceded_by_NUL(int enc)
    {
        // "xAttack\0": no leading NUL, so this is mid-word, not a standalone C-string.
        byte[] buf = Concat(Enc("x", enc), Enc(AttackCardProbeText.Label, enc), Nul(enc));
        int pos = enc;

        Assert.False(AttackCardProbeText.IsStandaloneHit(buf, pos, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_rejects_a_space_after_the_label(int enc)
    {
        // "Attack ": a space follows, not the NUL terminator.
        byte[] buf = Concat(Nul(enc), Enc(AttackCardProbeText.Label, enc), Enc(" ", enc));
        int pos = enc;

        Assert.False(AttackCardProbeText.IsStandaloneHit(buf, pos, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsStandaloneHit_does_not_throw_when_the_label_is_flush_with_buffer_end(int enc)
    {
        // No bytes at all after the label: the "char immediately after is NUL" proof is
        // unavailable within the buffer, so this must be rejected rather than guessed at, and
        // above all must never throw.
        byte[] buf = Concat(Nul(enc), Enc(AttackCardProbeText.Label, enc));
        int pos = enc;

        Assert.False(AttackCardProbeText.IsStandaloneHit(buf, pos, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadDesc_reads_the_NUL_terminated_string_after_the_label(int enc)
    {
        const string descText = "Deal damage with the wielded weapon.";
        byte[] buf = Concat(Enc(descText, enc), Nul(enc));

        var (text, chars) = AttackCardProbeText.ReadDesc(buf, 0, enc, Cap);

        Assert.Equal(descText, text);
        Assert.Equal(descText.Length, chars);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadDesc_caps_at_the_requested_char_count(int enc)
    {
        string longRun = new string('A', 200);
        byte[] buf = Enc(longRun, enc);   // never terminates within the buffer

        var (text, chars) = AttackCardProbeText.ReadDesc(buf, 0, enc, Cap);

        Assert.Equal(Cap, chars);
        Assert.Equal(Cap, text.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadDesc_returns_empty_for_an_immediate_NUL(int enc)
    {
        byte[] buf = Nul(enc);

        var (text, chars) = AttackCardProbeText.ReadDesc(buf, 0, enc, Cap);

        Assert.Equal("", text);
        Assert.Equal(0, chars);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReadDesc_does_not_throw_when_the_buffer_ends_without_a_NUL(int enc)
    {
        const string descText = "no terminator here";
        byte[] buf = Enc(descText, enc);

        var (text, chars) = AttackCardProbeText.ReadDesc(buf, 0, enc, Cap);

        Assert.Equal(descText, text);
        Assert.Equal(descText.Length, chars);
    }

    [Fact]
    public void ReadDesc_out_of_range_start_returns_empty()
    {
        byte[] buf = Enc("Attack", 1);

        var (text, chars) = AttackCardProbeText.ReadDesc(buf, buf.Length + 10, 1, Cap);

        Assert.Equal("", text);
        Assert.Equal(0, chars);
    }

    [Fact]
    public void FormatContext_contains_the_hex_of_a_known_byte()
    {
        byte[] label = Enc(AttackCardProbeText.Label, 1);
        var buf = new byte[label.Length + 60];
        Array.Copy(label, buf, label.Length);
        buf[label.Length + 5] = 0xAB;   // a known marker byte right after the hit

        string ctx = AttackCardProbeText.FormatContext(buf, 0, 1);

        Assert.Contains("AB", ctx);
    }

    [Fact]
    public void FormatContext_does_not_throw_at_buffer_edges()
    {
        byte[] label = Enc(AttackCardProbeText.Label, 1);

        // Hit is the entire buffer: no room before or after.
        string ctx1 = AttackCardProbeText.FormatContext(label, 0, 1);
        Assert.NotNull(ctx1);

        // pos beyond the buffer entirely (defensive: must not throw).
        string ctx2 = AttackCardProbeText.FormatContext(label, label.Length + 100, 1);
        Assert.NotNull(ctx2);
    }

    private static byte[] Enc(string s, int enc) => ByteScan.Enc(s, enc);
    private static byte[] Nul(int enc) => new byte[enc];

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        int at = 0;
        foreach (var p in parts) { Array.Copy(p, 0, result, at, p.Length); at += p.Length; }
        return result;
    }
}
