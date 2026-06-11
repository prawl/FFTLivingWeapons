using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Pure policy layer for the Treasure Master module -- no memory access, so every rule is
/// pinned here as a closed-form truth table. Covers:
///
/// (1) MapIdValid -- 1..127 are the FFTHandsFree LiveBattleMapId valid range; 0 is uninitialized,
///     128+ invalid; exact boundary checks at 0, 1, 127, 128 plus midpoints.
///
/// (2) Fnv1a64 -- standard FNV-1a 64-bit hash, shared verbatim with the Python capture tool so
///     the two implementations can never silently drift. Three pinned vectors: empty, "a", "foobar".
///
/// (3) AddrState / ClassifyAddr -- per-byte safety contract over the only legitimate values
///     {0x00, 0x01, 0x80, 0x81}. Bit 0x80 set with no other high bits -> Held; 0x00/0x01 ->
///     Resting; anything else -> Foreign (never written). Full truth table over representative
///     bytes.
///
/// (4) WantWrite -- OR-only invariant: cur | 0x80 always has bit 0x80 set; no other bits are
///     ever cleared.
///
/// (5) DecideArm -- three-path disarm logic: all-ok -> Arm; any Foreign -> immediate Disarm;
///     unreadables only -> Retry with grace until attemptCap then Disarm. Matrix covers all
///     transition edges.
///
/// (6) BuildKeyMatches -- exact equality on both TimeDateStamp and SizeOfImage; a single field
///     mismatch is a hard disarm.
/// </summary>
public class TreasureMasterPolicyTests
{
    // ---- (1) MapIdValid ----

    [Theory]
    [InlineData(0,   false)]   // uninitialized
    [InlineData(1,   true)]    // low boundary
    [InlineData(64,  true)]    // midpoint
    [InlineData(74,  true)]    // The Siedge Weald (known real map id)
    [InlineData(127, true)]    // high boundary
    [InlineData(128, false)]   // first invalid
    [InlineData(200, false)]
    [InlineData(255, false)]
    public void MapIdValid_accepts_1_to_127_only(byte id, bool expected)
    {
        Assert.Equal(expected, TreasureMaster.MapIdValid(id));
    }

    // ---- (2) Fnv1a64 -- pinned vectors (shared with Python self-test) ----

    [Fact]
    public void Fnv1a64_empty_input_returns_offset_basis()
    {
        Assert.Equal(0xcbf29ce484222325UL, TreasureMaster.Fnv1a64(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Fnv1a64_ascii_a()
    {
        Assert.Equal(0xaf63dc4c8601ec8cUL, TreasureMaster.Fnv1a64("a"u8));
    }

    [Fact]
    public void Fnv1a64_ascii_foobar()
    {
        Assert.Equal(0x85944171f73967e8UL, TreasureMaster.Fnv1a64("foobar"u8));
    }

    // ---- (3) AddrState / ClassifyAddr -- full truth table ----
    // InlineData carries ints (enums are internal; the cast is inside the body).

    [Theory]
    [InlineData(0x00, 0)]   // Resting
    [InlineData(0x01, 0)]   // Resting
    [InlineData(0x02, 2)]   // Foreign -- low-bit noise but high byte clear
    [InlineData(0x40, 2)]   // Foreign
    [InlineData(0x7F, 2)]   // Foreign
    [InlineData(0x80, 1)]   // Held
    [InlineData(0x81, 1)]   // Held
    [InlineData(0x82, 2)]   // Foreign -- 0x80 set but extra bits present
    [InlineData(0xC1, 2)]   // Foreign
    [InlineData(0xFF, 2)]   // Foreign
    public void ClassifyAddr_full_truth_table(byte cur, int expectedOrdinal)
    {
        // Ordinals: Resting=0, Held=1, Foreign=2 (declaration order in the enum)
        Assert.Equal((TreasureMaster.AddrState)expectedOrdinal, TreasureMaster.ClassifyAddr(cur));
    }

    // ---- (4) WantWrite -- OR-only invariant ----

    [Theory]
    [InlineData(0x00, 0x80)]
    [InlineData(0x01, 0x81)]
    public void WantWrite_sets_0x80_on_resting_bytes(byte cur, byte expected)
    {
        Assert.Equal(expected, TreasureMaster.WantWrite(cur));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x80)]
    [InlineData(0x81)]
    [InlineData(0x40)]
    [InlineData(0xFF)]
    public void WantWrite_never_produces_a_value_without_0x80(byte input)
    {
        Assert.NotEqual(0, TreasureMaster.WantWrite(input) & 0x80);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x80)]
    [InlineData(0x81)]
    [InlineData(0xAB)]
    [InlineData(0xFF)]
    public void WantWrite_never_clears_any_bit_that_was_already_set(byte input)
    {
        byte result = TreasureMaster.WantWrite(input);
        // OR-only: every bit set in input must still be set in result
        Assert.Equal(input & result, input);
    }

    // ---- (5) DecideArm -- matrix ----

    [Fact]
    public void DecideArm_all_ok_returns_Arm()
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Arm,
            TreasureMaster.DecideArm(okCount: 6, foreignCount: 0, unreadableCount: 0,
                attempt: 0, attemptCap: 5));
    }

    [Theory]
    [InlineData(1)]   // one Foreign is enough
    [InlineData(3)]
    public void DecideArm_any_Foreign_returns_immediate_Disarm(int foreignCount)
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Disarm,
            TreasureMaster.DecideArm(okCount: 4, foreignCount: foreignCount,
                unreadableCount: 0, attempt: 0, attemptCap: 5));
    }

    [Fact]
    public void DecideArm_Foreign_disarms_even_at_attempt_zero()
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Disarm,
            TreasureMaster.DecideArm(okCount: 0, foreignCount: 1,
                unreadableCount: 0, attempt: 0, attemptCap: 10));
    }

    [Theory]
    [InlineData(0, 5)]    // attempt 0 of cap 5 -> Retry
    [InlineData(3, 5)]    // attempt 3 of cap 5 -> Retry
    [InlineData(4, 5)]    // attempt 4 of cap 5 -> Retry (< cap, not yet disarmed)
    public void DecideArm_unreadable_only_retries_before_cap(int attempt, int attemptCap)
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 0, foreignCount: 0,
                unreadableCount: 3, attempt: attempt, attemptCap: attemptCap));
    }

    [Theory]
    [InlineData(5, 5)]    // attempt == cap -> Disarm
    [InlineData(6, 5)]    // attempt > cap -> Disarm
    public void DecideArm_unreadable_only_disarms_at_or_past_cap(int attempt, int attemptCap)
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Disarm,
            TreasureMaster.DecideArm(okCount: 0, foreignCount: 0,
                unreadableCount: 3, attempt: attempt, attemptCap: attemptCap));
    }

    [Fact]
    public void DecideArm_Foreign_beats_unreadable_for_immediate_Disarm()
    {
        // Even one foreign with unreadables present -> Disarm, not Retry
        Assert.Equal(TreasureMaster.ArmVerdict.Disarm,
            TreasureMaster.DecideArm(okCount: 0, foreignCount: 1,
                unreadableCount: 5, attempt: 0, attemptCap: 10));
    }

    [Fact]
    public void DecideArm_ok_plus_unreadable_with_no_foreign_retries()
    {
        // Mix of ok + unreadable but no foreign -> Retry (unread may clear on next tick)
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 3, foreignCount: 0,
                unreadableCount: 2, attempt: 1, attemptCap: 5));
    }

    // ---- (6) BuildKeyMatches ----

    [Fact]
    public void BuildKeyMatches_identical_keys_return_true()
    {
        Assert.True(TreasureMaster.BuildKeyMatches(
            dsTimeDateStamp: 0xAABBCCDD, dsSizeOfImage: 0x00180000,
            liveTimeDateStamp: 0xAABBCCDD, liveSizeOfImage: 0x00180000));
    }

    [Fact]
    public void BuildKeyMatches_stamp_mismatch_returns_false()
    {
        Assert.False(TreasureMaster.BuildKeyMatches(
            dsTimeDateStamp: 0xAABBCCDD, dsSizeOfImage: 0x00180000,
            liveTimeDateStamp: 0xAABBCCDE, liveSizeOfImage: 0x00180000));
    }

    [Fact]
    public void BuildKeyMatches_size_mismatch_returns_false()
    {
        Assert.False(TreasureMaster.BuildKeyMatches(
            dsTimeDateStamp: 0xAABBCCDD, dsSizeOfImage: 0x00180000,
            liveTimeDateStamp: 0xAABBCCDD, liveSizeOfImage: 0x00180001));
    }

    [Fact]
    public void BuildKeyMatches_both_mismatch_returns_false()
    {
        Assert.False(TreasureMaster.BuildKeyMatches(
            dsTimeDateStamp: 0xAABBCCDD, dsSizeOfImage: 0x00180000,
            liveTimeDateStamp: 0x11223344, liveSizeOfImage: 0x001A0000));
    }

    [Fact]
    public void BuildKeyMatches_zeroed_dataset_matches_zeroed_live()
    {
        // degenerate / uninitialized -> both zero still matches (boundary sanity)
        Assert.True(TreasureMaster.BuildKeyMatches(0, 0, 0, 0));
    }
}
