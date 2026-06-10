using System;
using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Yoichi Bow's "Barrage" signature. At +3 the wielder gains Barrage (JobCommand ability 358)
/// injected into their current job's 25-byte JobCommand record. The injection is session-only
/// (the table rebuilds at boot) but is re-asserted each tick (idempotent hold). The learned bit
/// is SET once and never cleared (the bow teaches permanently). On grant end, the original record
/// bytes are restored.
///
/// PROVEN mechanics (memory barrage-jobcommand-injection, barrage_probe.py):
///   Layout: ABILITY_BASE + rec*25 => AbilityId1..16 at bytes [0..15]; ExtAb u16 at -3,-2;
///   ExtRSM at -1. MSB-first per byte: slot i extend bit = bit(15-i) of ExtAb u16.
///   Ability 358: byte = 102, extend bit set. Wrong-bit tell = ghost Vengeance menu entries.
///
/// Pure jobs in Barrage.Policy.cs:
///   (1) IsActive: gates on grantCommandAbilityId > 0 AND tier >= AtTier.
///   (2) FindEmptySlot: first slot where byte==0 and extend bit==0 (1-indexed, 1..16).
///   (3) SlotByte: the byte value for ability 358 = 358 & 0xFF = 102.
///   (4) ExtendBit: for MSB-first u16, slot i(0-indexed) uses bit (15-i).
///   (5) NeedsInject: slot has the right byte and extend bit set -> already injected (idempotent).
///   (6) Never-re-save: save 25 bytes ONCE; if already saved, do not overwrite with injected state.
///   (7) LearnedBit: slot 10 = byte 1 bit 0x40 (byte1 of the action-abilities bitfield MSB-first).
/// </summary>
public class BarrageTests
{
    private static WeaponSignature BarrageSig(int grantAbility = 358, int atTier = 3) =>
        new() { AtTier = atTier, GrantCommandAbilityId = grantAbility, DisplayLabel = "Barrage" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Barrage.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_grantAbility_zero()
        => Assert.False(Barrage.IsActive(new WeaponSignature { GrantCommandAbilityId = 0, AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Barrage.IsActive(BarrageSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Barrage.IsActive(BarrageSig(atTier: 3), tier: 3));
        Assert.True(Barrage.IsActive(BarrageSig(atTier: 3), tier: 4));
    }

    // ---- (2) FindEmptySlot: first slot with byte==0 and extend bit==0 (1-indexed) ----

    [Fact]
    public void FindEmptySlot_returns_1_when_all_empty()
    {
        // 16 ability bytes all zero, ExtAb=0 -> slot 1 (index 0) is empty
        Assert.Equal(1, Barrage.FindEmptySlot(new byte[16], extAb: 0));
    }

    [Fact]
    public void FindEmptySlot_skips_filled_slots()
    {
        var ab = new byte[16];
        ab[0] = 100;   // slot 1 filled
        ab[1] = 50;    // slot 2 filled
        Assert.Equal(3, Barrage.FindEmptySlot(ab, extAb: 0));
    }

    [Fact]
    public void FindEmptySlot_skips_slot_with_set_extend_bit()
    {
        // Slot 1 has byte 0 but extend bit set -> it's using ability 256, not empty.
        var ab = new byte[16];
        // ab[0]=0 but extAb bit15 (slot0, 1-indexed slot 1) set
        ushort extAb = 0x8000;   // bit 15 set -> slot 1 is a 256+ ability with byte 0 -> not empty
        Assert.Equal(2, Barrage.FindEmptySlot(ab, extAb));
    }

    [Fact]
    public void FindEmptySlot_returns_minus1_when_all_filled()
    {
        var ab = new byte[16];
        for (int i = 0; i < 16; i++) ab[i] = (byte)(i + 1);
        Assert.Equal(-1, Barrage.FindEmptySlot(ab, extAb: 0));
    }

    // ---- (3) SlotByte: ability 358 -> low byte 102 ----

    [Fact]
    public void SlotByte_is_low_byte_of_ability_id()
    {
        Assert.Equal((byte)102, Barrage.SlotByte(358));
        Assert.Equal((byte)50, Barrage.SlotByte(50));    // ability < 256: byte == id
        Assert.Equal((byte)0, Barrage.SlotByte(256));    // 256 & 0xFF = 0
    }

    // ---- (4) ExtendBit: MSB-first u16 slot numbering ----
    // Slot i (0-indexed): bit = 15-i. Slot 0 -> bit 15 (0x8000), slot 15 -> bit 0 (0x0001).

    [Theory]
    [InlineData(0, 0x8000)]
    [InlineData(1, 0x4000)]
    [InlineData(9, 0x0040)]    // slot 9 (0-indexed) = 1-indexed slot 10; bit 6 = 0x0040
    [InlineData(10, 0x0020)]
    [InlineData(15, 0x0001)]
    public void ExtendBit_is_msb_first_per_slot(int slotIdx, int expectedBit)
        => Assert.Equal((ushort)expectedBit, Barrage.ExtendBit(slotIdx));

    // ---- (5) NeedsInject: idempotent check ----

    [Fact]
    public void NeedsInject_true_when_slot_is_empty()
        => Assert.True(Barrage.NeedsInject(slotByte: 0, extAb: 0, slotIdx: 0));

    [Fact]
    public void NeedsInject_false_when_already_injected_correctly()
    {
        // Ability 358: slotByte=102 + extend bit set at slotIdx=0 -> bit15 = 0x8000
        Assert.False(Barrage.NeedsInject(slotByte: 102, extAb: 0x8000, slotIdx: 0));
    }

    [Fact]
    public void NeedsInject_true_when_byte_correct_but_extend_bit_missing()
        => Assert.True(Barrage.NeedsInject(slotByte: 102, extAb: 0, slotIdx: 0));

    // ---- (6) Never-re-save: do not overwrite the saved record if already saved ----

    [Fact]
    public void BarrageState_never_overwrites_saved_record()
    {
        var state = new BarrageState();
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 };
        state.Save(recId: 27, record: original);
        Assert.True(state.HasSaved(27));

        // Try to save again with different bytes -- must not overwrite.
        var modified = new byte[25];
        state.Save(recId: 27, record: modified);
        Assert.Equal(original, state.GetSaved(27));
    }

    [Fact]
    public void BarrageState_clear_releases_saved_record()
    {
        var state = new BarrageState();
        state.Save(27, new byte[25]);
        state.Clear();
        Assert.False(state.HasSaved(27));
    }

    // ---- (7) LearnedBit: slot 10 is byte1 bit6 (0x40) ----

    [Fact]
    public void LearnedByteIndex_for_slot10_is_1()
        => Assert.Equal(1, Barrage.LearnedByteIndex(slotIdx1: 10));

    [Fact]
    public void LearnedBitMask_for_slot10_is_0x40()
        => Assert.Equal((byte)0x40, Barrage.LearnedBitMask(slotIdx1: 10));

    [Fact]
    public void LearnedByteIndex_for_slot1_is_0()
        => Assert.Equal(0, Barrage.LearnedByteIndex(slotIdx1: 1));

    [Fact]
    public void LearnedByteIndex_for_slot8_is_0()
        => Assert.Equal(0, Barrage.LearnedByteIndex(slotIdx1: 8));

    [Fact]
    public void LearnedByteIndex_for_slot9_is_1()
        => Assert.Equal(1, Barrage.LearnedByteIndex(slotIdx1: 9));

    // ---- Pinned buffer: inject ability 358 into slot 1 of a 25-byte record ----

    [Fact]
    public void Inject_writes_ability_byte_and_extend_bit_to_slot()
    {
        // A 28-byte buffer: [extAb_lo][extAb_hi][extRSM][ab0..15][rsm0..5]
        // Ability bytes start at offset 3.
        var buf = new byte[28];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        long flagAddr = h.AddrOfPinnedObject().ToInt64();   // base = flag bytes
        long abBase = flagAddr + 3;                          // ability bytes
        try
        {
            // Inject ability 358 into slot 0 (first slot, 0-indexed).
            Barrage.InjectSlot(flagAddr, abBase, slotIdx: 0, abilityId: 358);

            // Ability byte at slot 0 should be 102 (358 & 0xFF).
            Assert.Equal((byte)102, buf[3]);

            // ExtAb u16 at bytes 0-1 should have bit15 set (MSB-first, slot 0).
            ushort extAb = (ushort)(buf[0] | (buf[1] << 8));
            Assert.True((extAb & 0x8000) != 0);
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Restore_writes_back_the_saved_25_bytes_including_flags()
    {
        var original = new byte[25];
        for (int i = 0; i < 25; i++) original[i] = (byte)(i + 10);
        var buf = new byte[25];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        long flagAddr = h.AddrOfPinnedObject().ToInt64();
        try
        {
            Barrage.RestoreRecord(flagAddr, original);
            Assert.Equal(original, buf);
        }
        finally { h.Free(); }
    }
}
