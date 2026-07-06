using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Pure-half suite for TurnOwnerSpike (LW-31 stage 2's live-pass blocker, docs/TODO.md Now
/// entry): the ChangeGate (byte-array and string forms) and the sampling throttle math, plus
/// light coverage of the deterministic formatting helpers. The spike body itself
/// (TurnOwnerSpike.cs) is LWDEV-only and untestable by design; this exercises every pure
/// comparison and format that body stays thin over.
/// </summary>
public class TurnOwnerProbeTests
{
    // --- byte[] ChangeGate ---

    [Fact]
    public void Changed_bytes_first_sample_always_logs()
        => Assert.True(TurnOwnerProbe.Changed(null, new byte[] { 1, 2, 3 }));

    [Fact]
    public void Changed_bytes_identical_suppresses()
        => Assert.False(TurnOwnerProbe.Changed(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3 }));

    [Fact]
    public void Changed_bytes_any_difference_logs()
        => Assert.True(TurnOwnerProbe.Changed(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 4 }));

    [Fact]
    public void Changed_bytes_length_difference_logs()
        => Assert.True(TurnOwnerProbe.Changed(new byte[] { 1, 2, 3 }, new byte[] { 1, 2, 3, 4 }));

    // --- string ChangeGate (the CT snapshot / register snapshot lines) ---

    [Theory]
    [InlineData(null, "a", true)]
    [InlineData("a", "a", false)]
    [InlineData("a", "b", true)]
    public void Changed_strings_matches_the_gate_contract(string? last, string current, bool expected)
        => Assert.Equal(expected, TurnOwnerProbe.Changed(last, current));

    // --- sampling throttle (4 samples/second is a 250ms interval) ---

    [Theory]
    [InlineData(0L, null, 4, true)]        // first sample always fires
    [InlineData(1000L, 900L, 4, false)]    // 100ms elapsed, under the 250ms interval
    [InlineData(1249L, 1000L, 4, false)]   // 249ms elapsed, still under
    [InlineData(1250L, 1000L, 4, true)]    // exactly 250ms elapsed, boundary is inclusive
    [InlineData(2000L, 1000L, 4, true)]    // well past the interval
    public void ShouldSample_throttles_to_the_requested_rate(long nowMs, long? lastSampleMs, int samplesPerSecond, bool expected)
        => Assert.Equal(expected, TurnOwnerProbe.ShouldSample(nowMs, lastSampleMs, samplesPerSecond));

    // --- formatting (pure, deterministic; cheap extra confidence beyond the required gate/throttle tests) ---

    [Fact]
    public void FormatCtSnapshot_renders_one_line_per_valid_slot_with_the_shared_acted_value()
    {
        var slots = new List<(int slot, int lvl, int br, int fa, int ct)>
        {
            (0, 12, 34, 56, 78),
            (3, 99, 1, 1, 100),
        };
        string line = TurnOwnerProbe.FormatCtSnapshot(slots, acted: 1);
        Assert.Equal("turn-owner-probe: ct slots=[s0:12/34/56 ct=78 acted=1, s3:99/1/1 ct=100 acted=1]", line);
    }

    [Fact]
    public void FormatCtSnapshot_renders_an_empty_bracket_when_no_slot_is_valid()
        => Assert.Equal("turn-owner-probe: ct slots=[]",
            TurnOwnerProbe.FormatCtSnapshot(new List<(int slot, int lvl, int br, int fa, int ct)>(), acted: 0));

    [Fact]
    public void FormatRegisterSnapshot_renders_the_three_watched_fields()
        => Assert.Equal("turn-owner-probe: register nameId=42 arrivalTick=7 trusted=True",
            TurnOwnerProbe.FormatRegisterSnapshot(42, 7, true));

    [Fact]
    public void FormatCursorDump_reports_a_short_read_instead_of_throwing()
        => Assert.Equal("(short read)", TurnOwnerProbe.FormatCursorDump(new byte[4]));
}
