using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// HeaderProbeText, the pure half of the LW-27 header-repaint research instrument
/// (docs/TODO.md Now section). The #if LWDEV shell (HeaderSpike.cs) is not visible to the test
/// build (LWDEV is not defined for dotnet test), so only the pure static helpers are exercised
/// here, mirroring FlavorProbeTextTests' arrangement for the P4 flavor probe.
/// </summary>
public class HeaderProbeTextTests
{
    // THE LOAD-BEARING TEST: guarantees the in-place overwrite can never be mis-sized. Both
    // encodings must produce a same-length payload/pattern pair, or a write could corrupt
    // whatever buffer content follows the header label.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PayloadBytes_length_matches_Pattern_length(int enc)
    {
        byte[] pattern = HeaderProbeText.Pattern(enc);
        byte[] payload = HeaderProbeText.PayloadBytes(enc);

        Assert.Equal(pattern.Length, payload.Length);
        Assert.True(payload.Length > 0);   // non-vacuous: both encodings actually produce bytes
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsExactHit_accepts_the_label_mid_buffer(int enc)
    {
        byte[] before = ByteScan.Enc("XX ", enc);
        byte[] label = ByteScan.Enc(HeaderProbeText.Label, enc);
        byte[] after = ByteScan.Enc(" YY", enc);
        byte[] buf = Concat(before, label, after);

        Assert.True(HeaderProbeText.IsExactHit(buf, before.Length, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsExactHit_rejects_a_longer_word_with_a_letter_following(int enc)
    {
        byte[] before = ByteScan.Enc("XX ", enc);
        byte[] word = ByteScan.Enc(HeaderProbeText.Label + "s", enc);   // "Descriptions"
        byte[] buf = Concat(before, word);

        Assert.False(HeaderProbeText.IsExactHit(buf, before.Length, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsExactHit_rejects_a_hit_preceded_by_a_letter(int enc)
    {
        byte[] leader = ByteScan.Enc("X", enc);
        byte[] word = ByteScan.Enc("X" + HeaderProbeText.Label, enc);   // "XDescription"

        Assert.False(HeaderProbeText.IsExactHit(word, leader.Length, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsExactHit_handles_a_hit_flush_at_buffer_start(int enc)
    {
        byte[] label = ByteScan.Enc(HeaderProbeText.Label, enc);
        byte[] after = ByteScan.Enc(" YY", enc);
        byte[] buf = Concat(label, after);

        Assert.True(HeaderProbeText.IsExactHit(buf, 0, enc));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsExactHit_handles_a_hit_flush_at_buffer_end(int enc)
    {
        byte[] before = ByteScan.Enc("XX ", enc);
        byte[] label = ByteScan.Enc(HeaderProbeText.Label, enc);
        byte[] buf = Concat(before, label);

        Assert.True(HeaderProbeText.IsExactHit(buf, before.Length, enc));
    }

    [Fact]
    public void FormatContext_contains_the_hex_of_a_known_byte()
    {
        byte[] label = ByteScan.Enc(HeaderProbeText.Label, 1);
        var buf = new byte[label.Length + 60];
        Array.Copy(label, buf, label.Length);
        buf[label.Length + 5] = 0xAB;   // a known marker byte right after the hit

        string ctx = HeaderProbeText.FormatContext(buf, 0, 1);

        Assert.Contains("AB", ctx);
    }

    [Fact]
    public void FormatContext_does_not_throw_at_buffer_edges()
    {
        byte[] label = ByteScan.Enc(HeaderProbeText.Label, 1);

        // Hit is the entire buffer: no room before or after.
        string ctx1 = HeaderProbeText.FormatContext(label, 0, 1);
        Assert.NotNull(ctx1);

        // pos beyond the buffer entirely (defensive: must not throw).
        string ctx2 = HeaderProbeText.FormatContext(label, label.Length + 100, 1);
        Assert.NotNull(ctx2);
    }

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
