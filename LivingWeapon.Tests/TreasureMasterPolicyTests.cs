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
/// (5) DecideArm -- two-outcome arm gate: okCount >= minPlausible -> Arm; otherwise -> Retry.
///     Foreign bytes are NOT a disarm at arm time (they are off-screen render bytes and will
///     return to Resting when the tile scrolls back into view). Matrix covers quorum edges
///     and mixed foreign+ok counts.
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
    // Foreign bytes no longer trigger a Disarm: they are off-screen render bytes that return
    // to Resting when the camera pans back. The only outcomes are Arm and Retry.
    // Quorum: okCount >= minPlausible -> Arm; anything else -> Retry (infinite patience).

    [Fact]
    public void DecideArm_ok_at_or_above_minPlausible_returns_Arm()
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Arm,
            TreasureMaster.DecideArm(okCount: 6, foreignCount: 0, unreadableCount: 0,
                minPlausible: 4));
    }

    [Fact]
    public void DecideArm_ok_exactly_minPlausible_returns_Arm()
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Arm,
            TreasureMaster.DecideArm(okCount: 4, foreignCount: 0, unreadableCount: 0,
                minPlausible: 4));
    }

    [Fact]
    public void DecideArm_ok_below_minPlausible_returns_Retry()
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 3, foreignCount: 0, unreadableCount: 0,
                minPlausible: 4));
    }

    [Theory]
    [InlineData(1)]   // one Foreign with enough ok -> still Arms (foreign ignored for verdict)
    [InlineData(3)]
    public void DecideArm_Foreign_with_ok_above_quorum_arms(int foreignCount)
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Arm,
            TreasureMaster.DecideArm(okCount: 4, foreignCount: foreignCount,
                unreadableCount: 0, minPlausible: 4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DecideArm_Foreign_with_ok_below_quorum_retries(int foreignCount)
    {
        // Foreign bytes don't disarm; quorum not met -> Retry
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 2, foreignCount: foreignCount,
                unreadableCount: 0, minPlausible: 4));
    }

    [Fact]
    public void DecideArm_all_foreign_no_ok_retries_not_disarms()
    {
        // All tiles off-screen at battle start: should wait, not permanently disarm.
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 0, foreignCount: 5,
                unreadableCount: 0, minPlausible: 4));
    }

    [Fact]
    public void DecideArm_unreadable_only_retries()
    {
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 0, foreignCount: 0,
                unreadableCount: 3, minPlausible: 4));
    }

    [Fact]
    public void DecideArm_ok_plus_unreadable_with_quorum_arms()
    {
        // Enough ok addrs even with some unreadable -> Arm
        Assert.Equal(TreasureMaster.ArmVerdict.Arm,
            TreasureMaster.DecideArm(okCount: 5, foreignCount: 0,
                unreadableCount: 2, minPlausible: 4));
    }

    [Fact]
    public void DecideArm_ok_plus_foreign_plus_unreadable_below_quorum_retries()
    {
        // No disarm path: below quorum with foreign present -> Retry
        Assert.Equal(TreasureMaster.ArmVerdict.Retry,
            TreasureMaster.DecideArm(okCount: 1, foreignCount: 3,
                unreadableCount: 2, minPlausible: 4));
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

    // ---- (7) MaskedTerrainHash -- pinned cross-language vector ----
    // Grid: 208 records x 7 bytes each.  The masked hash is FNV-1a64 over
    // field-0 bytes only (byte at offset i*7 for i in 0..207).
    //
    // Pinned test vector: 14-byte synthetic buffer (2 records of 7 bytes):
    //   record 0: 01 02 03 04 05 06 07
    //   record 1: 11 12 13 14 15 16 17
    // field-0 bytes = [0x01, 0x11] => Fnv1a64([0x01, 0x11]) = 0x082f3307b4e8a9a7
    // The Python self-test uses the same constant (never let the two drift).

    private const ulong MaskedHashVector = 0x082f3307b4e8a9a7UL;

    [Fact]
    public void MaskedTerrainHash_pinned_two_record_vector()
    {
        var raw = new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,   // record 0
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,   // record 1
        };
        Assert.Equal(MaskedHashVector, TreasureMaster.MaskedTerrainHash(raw));
    }

    [Fact]
    public void MaskedTerrainHash_equals_Fnv1a64_over_field0_bytes_only()
    {
        // Explicit: masking strips fields 1-6; only field-0 bytes feed the hash.
        var raw = new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        };
        byte[] field0 = { 0x01, 0x11 };
        ulong expected = TreasureMaster.Fnv1a64(field0);
        Assert.Equal(expected, TreasureMaster.MaskedTerrainHash(raw));
    }

    [Fact]
    public void MaskedTerrainHash_non_field0_mutation_does_not_change_hash()
    {
        // The actual incident: field 1 and field 6 change mid-battle, field 0 holds still.
        // v2 fingerprint must be immune to those changes.
        var raw1 = new byte[]
        {
            0x05, 0xAA, 0x00, 0x00, 0x00, 0x00, 0x00,   // record 0: field-0=0x05, fields 1-6=AA/0..
            0x03, 0xBB, 0x00, 0x00, 0x00, 0x00, 0xFF,   // record 1: field-0=0x03
        };
        var raw2 = new byte[]
        {
            0x05, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66,   // field-0 unchanged, fields 1-6 mutated
            0x03, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0x00,
        };
        Assert.Equal(
            TreasureMaster.MaskedTerrainHash(raw1),
            TreasureMaster.MaskedTerrainHash(raw2));
    }

    [Fact]
    public void MaskedTerrainHash_field0_mutation_changes_hash()
    {
        // Opposite case: if field-0 does change (true map geometry change), the hash changes.
        var raw1 = new byte[]
        {
            0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        var raw2 = new byte[]
        {
            0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,   // field-0 changed
            0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        Assert.NotEqual(
            TreasureMaster.MaskedTerrainHash(raw1),
            TreasureMaster.MaskedTerrainHash(raw2));
    }

    // ---- (8) MaskedTerrainHashV3 -- pinned cross-language vector ----
    // Grid: 208 records x 7 bytes each.  V3 hashes fields {2,3,4,5} (the static fields)
    // per record -- immune to field-0 (height), field-1 (slope), and field-6 (flow) which
    // all animate on water/lava maps.
    //
    // Pinned test vector: 14-byte synthetic buffer (2 records of 7 bytes):
    //   record 0: 01 02 03 04 05 06 07
    //   record 1: 11 12 13 14 15 16 17
    // fields {2,3,4,5} bytes = [03 04 05 06 13 14 15 16]
    //   (i%7 in {2,3,4,5}: indices 2,3,4,5,9,10,11,12)
    // result = Fnv1a64([03 04 05 06 13 14 15 16]) = 0x05708f90b5f5fac5
    // The Python self-test and gen_treasure_db.py use the same constant (never let them drift).

    private const ulong MaskedHashV3Vector = 0x05708f90b5f5fac5UL;

    [Fact]
    public void MaskedTerrainHashV3_pinned_two_record_vector()
    {
        var raw = new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,   // record 0
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,   // record 1
        };
        Assert.Equal(MaskedHashV3Vector, TreasureMaster.MaskedTerrainHashV3(raw));
    }

    [Fact]
    public void MaskedTerrainHashV3_equals_Fnv1a64_over_fields2345_bytes_only()
    {
        // Explicit: v3 masks to fields {2,3,4,5} per record; only those bytes feed the hash.
        var raw = new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
        };
        // Fields {2,3,4,5} of record 0: 0x03,0x04,0x05,0x06
        // Fields {2,3,4,5} of record 1: 0x13,0x14,0x15,0x16
        byte[] staticBytes = { 0x03, 0x04, 0x05, 0x06, 0x13, 0x14, 0x15, 0x16 };
        ulong expected = TreasureMaster.Fnv1a64(staticBytes);
        Assert.Equal(expected, TreasureMaster.MaskedTerrainHashV3(raw));
    }

    [Fact]
    public void MaskedTerrainHashV3_field0_mutation_does_not_change_hash()
    {
        // The water-map incident: field-0 (height) animates; v3 must be immune to it.
        var raw1 = new byte[]
        {
            0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x00,   // record 0: field-0=0x05
            0x07, 0x00, 0x0A, 0x0B, 0x0C, 0x0D, 0x00,   // record 1: field-0=0x07
        };
        var raw2 = new byte[]
        {
            0x99, 0x00, 0x01, 0x02, 0x03, 0x04, 0x00,   // field-0 changed (height animated)
            0xAB, 0x00, 0x0A, 0x0B, 0x0C, 0x0D, 0x00,   // field-0 changed
        };
        Assert.Equal(
            TreasureMaster.MaskedTerrainHashV3(raw1),
            TreasureMaster.MaskedTerrainHashV3(raw2));
    }

    [Fact]
    public void MaskedTerrainHashV3_field1_mutation_does_not_change_hash()
    {
        // Field-1 (slope) also animates on water maps; v3 must be immune.
        var raw1 = new byte[]
        {
            0x05, 0x10, 0x01, 0x02, 0x03, 0x04, 0x00,
        };
        var raw2 = new byte[]
        {
            0x05, 0xFF, 0x01, 0x02, 0x03, 0x04, 0x00,   // field-1 changed
        };
        Assert.Equal(
            TreasureMaster.MaskedTerrainHashV3(raw1),
            TreasureMaster.MaskedTerrainHashV3(raw2));
    }

    [Fact]
    public void MaskedTerrainHashV3_field6_mutation_does_not_change_hash()
    {
        // Field-6 (flow) animates on water maps; v3 must be immune.
        var raw1 = new byte[]
        {
            0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x10,
        };
        var raw2 = new byte[]
        {
            0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0xFF,   // field-6 changed (flow animated)
        };
        Assert.Equal(
            TreasureMaster.MaskedTerrainHashV3(raw1),
            TreasureMaster.MaskedTerrainHashV3(raw2));
    }

    [Fact]
    public void MaskedTerrainHashV3_field3_mutation_changes_hash()
    {
        // Fields {2,3,4,5} are static geometry -- a change means a genuinely different map.
        var raw1 = new byte[]
        {
            0x05, 0x00, 0x01, 0x02, 0x03, 0x04, 0x00,
        };
        var raw2 = new byte[]
        {
            0x05, 0x00, 0x01, 0xAA, 0x03, 0x04, 0x00,   // field-3 changed
        };
        Assert.NotEqual(
            TreasureMaster.MaskedTerrainHashV3(raw1),
            TreasureMaster.MaskedTerrainHashV3(raw2));
    }

    // ---- (9) FingerprintMatches dispatch: fpVer 2 -> v2, fpVer 3 -> v3 ----
    // (These are integration-level tests against ArmAudit; the heavy state-machine
    // tests that exercise the water-map regression live in TreasureMasterTests.cs.)
}
