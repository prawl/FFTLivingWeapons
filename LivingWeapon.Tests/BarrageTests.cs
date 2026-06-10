using System;
using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Yoichi Bow's "Barrage" signature. At +3 the wielder gains Barrage (JobCommand ability 358)
/// injected into their current job's 25-byte JobCommand record. The injection is session-only
/// (the table rebuilds at boot) and is re-asserted each tick (idempotent hold). The learned bit
/// is HELD each tick and never cleared: a menu ability purchase writes the learned block back
/// from a stale snapshot and wipes externally-set bits (proven live 2026-06-10), so set-once is
/// not enough. On grant end, the original record bytes are restored.
///
/// LIVE-PROVEN layout (2026-06-10 menu session; barrage_probe.py's "msb" flag is the WRONG order):
///   Record r: flags at ABILITY_BASE + r*25 - 3 = [ExtAb byte0][ExtAb byte1][ExtRSM], then
///   AbilityId1..16, RSMId1..6. Extend bits are MSB-first PER BYTE: byte0 = slots 1-8
///   (bit7 = slot 1), byte1 = slots 9-16 (bit7 = slot 9). As the LE-composed u16
///   (byte0 | byte1&lt;&lt;8): slot 10 = 0x4000. Anchors: Archer rec 8 = Aim+1..+8 stored as
///   bytes 150-157 with byte0=0xFF (= ids 406-413); injected slot 9 rendered as Aurablast (102)
///   until byte1 bit7 was set, then rendered as ability 358.
///
/// Job resolution (live anchors: Chemist purchase diff, Archer menu test, FFTHandsFree job dict):
///   roster job byte +0x02; generic band 74..92 in PSX wheel order. rec = job - 69,
///   jobIdx = job - 74, EXCEPT Dancer 92 shares jobIdx 17 with Bard. Mime 93 (no learnable
///   actions), story-canonical unique jobs, monsters, Dark Knight: unsupported -> no-op.
///   Learned triple at roster +0x32 + jobIdx*3: byte0 = slots 1-8, byte1 = slots 9-16, MSB-first.
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

    // ---- (2) TryResolveJob: roster job byte -> (JobCommand rec, learned jobIdx) ----
    // Live anchors: Archer 77 -> rec 8 (menu-verified), Chemist 75 -> jobIdx 1 (purchase diff).

    [Theory]
    [InlineData(74, 5, 0)]     // Squire -> Fundaments
    [InlineData(75, 6, 1)]     // Chemist -> Items (jobIdx LIVE-proven by High Ether purchase)
    [InlineData(76, 7, 2)]     // Knight -> Arts of War
    [InlineData(77, 8, 3)]     // Archer -> rec 8 (LIVE-proven: injected entry rendered in menu)
    [InlineData(80, 11, 6)]    // Black Mage -> Black Magicks
    [InlineData(83, 14, 9)]    // Thief -> Steal
    [InlineData(87, 18, 13)]   // Dragoon -> Jump
    [InlineData(91, 22, 17)]   // Bard -> Bardsong
    [InlineData(92, 23, 17)]   // Dancer -> Dance (rec differs from Bard, jobIdx SHARED = 17)
    public void TryResolveJob_maps_generic_band(int job, int expectedRec, int expectedJobIdx)
    {
        Assert.True(Barrage.TryResolveJob(job, out int rec, out int jobIdx));
        Assert.Equal(expectedRec, rec);
        Assert.Equal(expectedJobIdx, jobIdx);
    }

    [Theory]
    [InlineData(93)]    // Mime: no learnable actions
    [InlineData(0)]     // empty
    [InlineData(27)]    // a REC id, not a job id -- must not be confused
    [InlineData(73)]    // below band
    [InlineData(94)]    // monster band starts
    [InlineData(160)]   // Dark Knight: rec unmapped, unsupported for now
    public void TryResolveJob_rejects_outside_generic_band(int job)
        => Assert.False(Barrage.TryResolveJob(job, out _, out _));

    // ---- (3) FindEmptySlot: first slot with byte==0 and extend bit==0 (1-indexed) ----

    [Fact]
    public void FindEmptySlot_returns_1_when_all_empty()
        => Assert.Equal(1, Barrage.FindEmptySlot(new byte[16], extAb: 0));

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
        // Slot 1 has byte 0 but its extend bit set -> it holds ability 256, not empty.
        // Slot 1 = byte0 bit7 = 0x0080 in the LE-composed u16.
        var ab = new byte[16];
        Assert.Equal(2, Barrage.FindEmptySlot(ab, extAb: 0x0080));
    }

    [Fact]
    public void FindEmptySlot_archer_shape_picks_slot_9()
    {
        // Archer rec 8 (live dump): slots 1-8 = bytes 150-157 with byte0=0xFF (Aim+1..+8),
        // slots 9-16 empty with byte1=0x00. First empty = slot 9.
        var ab = new byte[16];
        for (int i = 0; i < 8; i++) ab[i] = (byte)(150 + i);
        Assert.Equal(9, Barrage.FindEmptySlot(ab, extAb: 0x00FF));
    }

    [Fact]
    public void FindEmptySlot_returns_minus1_when_all_filled()
    {
        var ab = new byte[16];
        for (int i = 0; i < 16; i++) ab[i] = (byte)(i + 1);
        Assert.Equal(-1, Barrage.FindEmptySlot(ab, extAb: 0));
    }

    // ---- (4) SlotByte: ability 358 -> low byte 102 ----

    [Fact]
    public void SlotByte_is_low_byte_of_ability_id()
    {
        Assert.Equal((byte)102, Barrage.SlotByte(358));
        Assert.Equal((byte)50, Barrage.SlotByte(50));    // ability < 256: byte == id
        Assert.Equal((byte)0, Barrage.SlotByte(256));    // 256 & 0xFF = 0
    }

    // ---- (5) ExtendBit: MSB-first PER BYTE (byte0 = slots 1-8, byte1 = slots 9-16) ----
    // As LE-composed u16 (byte0 | byte1<<8). Slot 10 (0-indexed 9) = 0x4000 -- the live-proven
    // anchor; the old whole-u16 formula (1 << (15-i)) put it at 0x0040 and rendered Aurablast.

    [Theory]
    [InlineData(0, 0x0080)]    // slot 1  = byte0 bit7
    [InlineData(1, 0x0040)]    // slot 2  = byte0 bit6
    [InlineData(7, 0x0001)]    // slot 8  = byte0 bit0
    [InlineData(8, 0x8000)]    // slot 9  = byte1 bit7 (LIVE-proven: flipped 358 into the menu)
    [InlineData(9, 0x4000)]    // slot 10 = byte1 bit6 (the memory's proven anchor)
    [InlineData(15, 0x0100)]   // slot 16 = byte1 bit0
    public void ExtendBit_is_msb_first_per_byte(int slotIdx, int expectedBit)
        => Assert.Equal((ushort)expectedBit, Barrage.ExtendBit(slotIdx));

    // ---- (6) NeedsInject: idempotent check ----

    [Fact]
    public void NeedsInject_true_when_slot_is_empty()
        => Assert.True(Barrage.NeedsInject(slotByte: 0, extAb: 0, slotIdx: 0));

    [Fact]
    public void NeedsInject_false_when_already_injected_correctly()
    {
        // Ability 358 at slotIdx 8 (slot 9): byte=102 + byte1 bit7 = 0x8000.
        Assert.False(Barrage.NeedsInject(slotByte: 102, extAb: 0x8000, slotIdx: 8));
    }

    [Fact]
    public void NeedsInject_true_when_byte_correct_but_extend_bit_missing()
        => Assert.True(Barrage.NeedsInject(slotByte: 102, extAb: 0, slotIdx: 8));

    [Fact]
    public void NeedsInject_true_when_wrong_bytes_extend_bit_set()
    {
        // The Aurablast symptom: byte present, but only OTHER extend bits set (0x00FF = slots 1-8).
        Assert.True(Barrage.NeedsInject(slotByte: 102, extAb: 0x00FF, slotIdx: 8));
    }

    // ---- (7) Never-re-save: do not overwrite the saved record if already saved ----

    [Fact]
    public void BarrageState_never_overwrites_saved_record()
    {
        var state = new BarrageState();
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 };
        state.Save(recId: 8, record: original);
        Assert.True(state.HasSaved(8));

        // Try to save again with different bytes -- must not overwrite.
        var modified = new byte[25];
        state.Save(recId: 8, record: modified);
        Assert.Equal(original, state.GetSaved(8));
    }

    [Fact]
    public void BarrageState_clear_releases_saved_record()
    {
        var state = new BarrageState();
        state.Save(8, new byte[25]);
        state.Clear();
        Assert.False(state.HasSaved(8));
    }

    // ---- (8) Learned bitfield: byte0 = slots 1-8, byte1 = slots 9-16, MSB-first ----
    // Slot 9 = byte1 bit7 (0x80): live-proven -- the bit we set rendered the entry as learned,
    // and the game's own purchase writeback wiped exactly that byte.

    [Theory]
    [InlineData(1, 0, 0x80)]
    [InlineData(8, 0, 0x01)]
    [InlineData(9, 1, 0x80)]    // Barrage's slot on the Archer record (live-proven)
    [InlineData(10, 1, 0x40)]
    [InlineData(16, 1, 0x01)]
    public void Learned_byte_and_mask_are_msb_first_per_byte(int slot1, int expectedByte, int expectedMask)
    {
        Assert.Equal(expectedByte, Barrage.LearnedByteIndex(slot1));
        Assert.Equal((byte)expectedMask, Barrage.LearnedBitMask(slot1));
    }

    // ---- Pinned buffer: inject ability 358 into a 25-byte record ----

    [Fact]
    public void Inject_writes_ability_byte_and_per_byte_extend_bit()
    {
        // A 28-byte buffer: [extAb_b0][extAb_b1][extRSM][ab0..15][rsm0..5]
        var buf = new byte[28];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        long flagAddr = h.AddrOfPinnedObject().ToInt64();   // base = flag bytes
        long abBase = flagAddr + 3;                          // ability bytes
        try
        {
            // Inject ability 358 into slotIdx 8 (slot 9 -- the Archer shape).
            Barrage.InjectSlot(flagAddr, abBase, slotIdx: 8, abilityId: 358);

            Assert.Equal((byte)102, buf[3 + 8]);     // ability byte
            Assert.Equal((byte)0x80, buf[1]);        // byte1 bit7 = slot 9 extend
            Assert.Equal((byte)0x00, buf[0]);        // byte0 untouched
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Inject_slot1_uses_byte0_bit7()
    {
        var buf = new byte[28];
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        long flagAddr = h.AddrOfPinnedObject().ToInt64();
        try
        {
            Barrage.InjectSlot(flagAddr, flagAddr + 3, slotIdx: 0, abilityId: 358);
            Assert.Equal((byte)102, buf[3]);
            Assert.Equal((byte)0x80, buf[0]);        // byte0 bit7 = slot 1 extend
            Assert.Equal((byte)0x00, buf[1]);
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Inject_preserves_existing_extend_bits()
    {
        // Archer rec 8: byte0 = 0xFF (Aim+N extends) must survive a slot-9 inject.
        var buf = new byte[28];
        buf[0] = 0xFF;
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        long flagAddr = h.AddrOfPinnedObject().ToInt64();
        try
        {
            Barrage.InjectSlot(flagAddr, flagAddr + 3, slotIdx: 8, abilityId: 358);
            Assert.Equal((byte)0xFF, buf[0]);
            Assert.Equal((byte)0x80, buf[1]);
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
