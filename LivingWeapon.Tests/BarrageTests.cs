using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Yoichi Bow's "Barrage" signature. At +3 the wielder gains Barrage (JobCommand ability 358)
/// injected into their current job's 25-byte JobCommand record. The injection is session-only
/// (the table rebuilds at boot) and is re-asserted each tick (idempotent hold). The learned bit
/// is HELD each tick and never cleared: a menu ability purchase writes the learned block back
/// from a stale snapshot and wipes externally-set bits (observed live 2026-06-10), so set-once is
/// not enough. On grant end, ONLY our own slot is released (ReleaseSlot) -- no snapshot, no
/// whole-record restore; see BarrageShadowBladeCollisionTests for why that matters when two
/// signatures share one record.
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
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

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

    // ---- THIEF-ONLY gate (card states "Barrage (Thief Only)") ----
    // A wielder qualifies when (primary job == Thief 83) OR (secondary record == Steal 14).

    [Fact]
    public void IsEligibleWielder_true_for_primary_thief()
        => Assert.True(Barrage.IsEligibleWielder(83, secondaryRecord: 0));

    [Fact]
    public void IsEligibleWielder_true_for_primary_thief_with_any_secondary()
        => Assert.True(Barrage.IsEligibleWielder(83, secondaryRecord: 11));

    [Fact]
    public void IsEligibleWielder_true_for_archer_with_thief_secondary()
        => Assert.True(Barrage.IsEligibleWielder(77, secondaryRecord: 14));

    [Fact]
    public void IsEligibleWielder_true_for_story_job_with_thief_secondary()
        => Assert.True(Barrage.IsEligibleWielder(1, secondaryRecord: 14));

    [Theory]
    [InlineData(77, 10)]   // Archer with some other secondary
    [InlineData(74, 10)]   // Squire with some other secondary
    [InlineData(80, 0)]    // Black Mage, no secondary
    [InlineData(87, 5)]    // Dragoon with Fundaments secondary
    [InlineData(1, 0)]     // Ramza / Mettle, no secondary
    [InlineData(0, 0)]     // empty
    public void IsEligibleWielder_false_when_neither_primary_nor_secondary_is_thief(int job, int sec)
        => Assert.False(Barrage.IsEligibleWielder(job, sec));

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
    [InlineData(76, 7, 2)]     // Knight -> Arts of War
    [InlineData(78, 9, 4)]     // Monk -> Martial Arts
    [InlineData(80, 11, 6)]    // Black Mage -> Black Magicks
    [InlineData(83, 14, 9)]    // Thief -> Steal (LIVE-proven: Barrage cast from Steal, twice)
    [InlineData(85, 16, 11)]   // Mystic -> Mystic Arts
    [InlineData(88, 19, 14)]   // Samurai -> Iaido
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

    // Special-executor commands silently drop foreign ability ids at confirm time.
    // OBSERVED LIVE for Aim (2026-06-10): 358, 102 Aurablast, 146 Focus, and 16 Fire all
    // swallowed from rec 8 slots 1/9/10/11 on two different units; the menu label renders but
    // targeting/preview are positional basic-attack cosmetics and execution no-ops. The id
    // whitelist is code-side (no writable tier table found in a full memory scan). Jump shares
    // Aim's tier-row structure; Items/Throw consult inventory; Arithmeticks is a bespoke picker.
    [Theory]
    [InlineData(75)]    // Chemist -> Items
    [InlineData(77)]    // Archer -> Aim (the observed live wall)
    [InlineData(87)]    // Dragoon -> Jump
    [InlineData(89)]    // Ninja -> Throw
    [InlineData(90)]    // Arithmetician -> Arithmeticks
    public void TryResolveJob_rejects_special_executor_jobs(int job)
        => Assert.False(Barrage.TryResolveJob(job, out _, out _));

    // ---- Secondary fallback: roster +0x07 holds the JobCommand rec id directly
    // (live-read 14 with Steal mounted). Barrage cast from an Archer's mounted Steal
    // secondary is LIVE-PROVEN -- the executor follows the skillset, not the slot. ----

    [Theory]
    [InlineData(14, 9)]    // Steal (the live-proven fallback)
    [InlineData(5, 0)]     // Fundaments
    [InlineData(7, 2)]     // Arts of War
    [InlineData(19, 14)]   // Iaido
    [InlineData(22, 17)]   // Bardsong
    [InlineData(23, 17)]   // Dance (shares Bard's jobIdx)
    [InlineData(25, 0)]    // Mettle ch1 variant (Mettle executed 358 in the morning proof)
    [InlineData(27, 0)]    // Mettle endgame variant
    public void TryResolveSecondary_maps_normal_records(int rec, int expectedJobIdx)
    {
        Assert.True(Barrage.TryResolveSecondary(rec, out int jobIdx));
        Assert.Equal(expectedJobIdx, jobIdx);
    }

    [Theory]
    [InlineData(6)]     // Items
    [InlineData(8)]     // Aim
    [InlineData(18)]    // Jump
    [InlineData(20)]    // Throw
    [InlineData(21)]    // Arithmeticks
    [InlineData(24)]    // Mime (empty record)
    [InlineData(0)]     // no secondary
    [InlineData(4)]     // empty record below the wheel
    [InlineData(28)]    // outside the supported set
    [InlineData(255)]
    public void TryResolveSecondary_rejects_special_and_invalid(int rec)
        => Assert.False(Barrage.TryResolveSecondary(rec, out _));

    [Fact]
    public void TryResolveGrant_prefers_the_primary_when_normal()
    {
        // Thief primary -> Steal record, even with some secondary mounted.
        Assert.True(Barrage.TryResolveGrant(83, secondaryRec: 11, out int rec, out int jobIdx, out bool viaSecondary));
        Assert.Equal(14, rec);
        Assert.Equal(9, jobIdx);
        Assert.False(viaSecondary);
    }

    [Fact]
    public void TryResolveGrant_falls_back_to_secondary_for_special_executor_primary()
    {
        // Archer with Steal mounted: the live-proven rescue.
        Assert.True(Barrage.TryResolveGrant(77, secondaryRec: 14, out int rec, out int jobIdx, out bool viaSecondary));
        Assert.Equal(14, rec);
        Assert.Equal(9, jobIdx);
        Assert.True(viaSecondary);
    }

    [Fact]
    public void TryResolveGrant_covers_story_unique_primaries_via_secondary()
    {
        // A story-job byte outside the generic band (e.g. Ramza in Mettle) still gets the
        // grant through a mounted normal secondary.
        Assert.True(Barrage.TryResolveGrant(1, secondaryRec: 14, out int rec, out int jobIdx, out bool viaSecondary));
        Assert.Equal(14, rec);
        Assert.True(viaSecondary);
    }

    [Theory]
    [InlineData(77, 6)]    // Archer with Items secondary: both special
    [InlineData(77, 0)]    // Archer with no secondary
    [InlineData(94, 0)]    // monster
    public void TryResolveGrant_fails_when_nothing_grantable(int job, int sec)
        => Assert.False(Barrage.TryResolveGrant(job, sec, out _, out _, out _));

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

    // ---- (7) BarrageState: atomic (RecId, SlotIdx) pair, set and cleared together ----
    // Retired here: the old Save/HasSaved/GetSaved snapshot API (and its "never-re-save"
    // invariant) fed the whole-record restore this change removes. There is nothing left to
    // snapshot -- ReleaseSlot needs no saved bytes, so BarrageState carries only the slot pointer.

    [Fact]
    public void BarrageState_starts_empty()
    {
        var state = new BarrageState();
        Assert.Equal(-1, state.RecId);
        Assert.Equal(-1, state.SlotIdx);
    }

    [Fact]
    public void BarrageState_set_moves_recId_and_slotIdx_together()
    {
        var state = new BarrageState();
        state.Set(recId: 7, slotIdx: 3);
        Assert.Equal(7, state.RecId);
        Assert.Equal(3, state.SlotIdx);
    }

    [Fact]
    public void BarrageState_clear_forgets_both_fields_together()
    {
        var state = new BarrageState();
        state.Set(recId: 7, slotIdx: 3);
        state.Clear();
        Assert.Equal(-1, state.RecId);
        Assert.Equal(-1, state.SlotIdx);
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
        using var rec = PinnedBuf.Of(28);
        long flagAddr = rec.Addr;                            // base = flag bytes
        long abBase = flagAddr + 3;                          // ability bytes

        // Inject ability 358 into slotIdx 8 (slot 9 -- the Archer shape).
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));

        Assert.Equal((byte)102, rec.Bytes[3 + 8]);   // ability byte
        Assert.Equal((byte)0x80, rec.Bytes[1]);      // byte1 bit7 = slot 9 extend
        Assert.Equal((byte)0x00, rec.Bytes[0]);      // byte0 untouched
    }

    [Fact]
    public void Inject_slot1_uses_byte0_bit7()
    {
        using var rec = PinnedBuf.Of(28);
        Assert.True(Barrage.InjectSlot(Live, rec.Addr, rec.Addr + 3, slotIdx: 0, abilityId: 358));
        Assert.Equal((byte)102, rec.Bytes[3]);
        Assert.Equal((byte)0x80, rec.Bytes[0]);      // byte0 bit7 = slot 1 extend
        Assert.Equal((byte)0x00, rec.Bytes[1]);
    }

    [Fact]
    public void Inject_preserves_existing_extend_bits()
    {
        // Archer rec 8: byte0 = 0xFF (Aim+N extends) must survive a slot-9 inject.
        using var rec = PinnedBuf.Of(28);
        rec.Bytes[0] = 0xFF;
        Assert.True(Barrage.InjectSlot(Live, rec.Addr, rec.Addr + 3, slotIdx: 8, abilityId: 358));
        Assert.Equal((byte)0xFF, rec.Bytes[0]);
        Assert.Equal((byte)0x80, rec.Bytes[1]);
    }

    [Fact]
    public void Inject_refuses_when_a_different_ability_already_occupies_the_slot()
    {
        // The symmetric guard: a slotIdx handed a stale index that now points at a REAL ability
        // (e.g. a table rebuild) must not be able to stomp it.
        using var rec = PinnedBuf.Of(28);
        rec.Bytes[3 + 8] = 200;   // some other ability's byte already sitting in slot 9
        Assert.False(Barrage.InjectSlot(Live, rec.Addr, rec.Addr + 3, slotIdx: 8, abilityId: 358));
        Assert.Equal((byte)200, rec.Bytes[3 + 8]);   // untouched
        Assert.Equal((byte)0x00, rec.Bytes[1]);       // no extend bit written either
    }

    // ---- ReleaseSlot: the slot-scoped replacement for the old whole-record restore ----
    // T1-T5 from the plan, plus the Aurablast (byte-matches-extend-disagrees) and all-or-nothing
    // guard cases the plan's own test list under-specified.

    [Fact]
    public void ReleaseSlot_zeroes_own_slot_byte_and_clears_own_extend_bit()   // T1
    {
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));

        Assert.Equal((byte)0, rec.Bytes[3 + 8]);
        Assert.Equal((byte)0x00, rec.Bytes[1]);   // byte1 bit7 (slot 9) cleared
    }

    [Fact]
    public void ReleaseSlot_leaves_every_other_byte_of_the_record_byte_identical()   // T2
    {
        // A non-zero, realistic record (Archer rec 8 shape: Aim+1..+8 in slots 1-8, byte0=0xFF)
        // with Barrage injected at slot 9 and arbitrary non-zero filler in the untouched tail --
        // "byte-identical" against an all-zero buffer would prove nothing.
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        rec.Bytes[0] = 0xFF;                                  // slots 1-8 extend bits (Aim+N)
        rec.Bytes[2] = 0x55;                                  // ExtRSM filler
        for (int i = 0; i < 8; i++) rec.Bytes[3 + i] = (byte)(150 + i);   // Aim+1..+8
        for (int i = 9; i < 16; i++) rec.Bytes[3 + i] = 0xAA;             // slots 10-16 filler
        for (int i = 0; i < 6; i++) rec.Bytes[3 + 16 + i] = 0x77;         // RSM filler
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));

        byte[] expected = (byte[])rec.Bytes.Clone();
        expected[3 + 8] = 0;      // only Barrage's own slot byte goes back to empty
        expected[1] = 0x00;       // only its own extend bit clears

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));
        Assert.Equal(expected, rec.Bytes);
    }

    [Fact]
    public void ReleaseSlot_leaves_other_slots_extend_bits_untouched()   // T3
    {
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        // Seed three OTHER slots (spanning both extend-flag bytes) with real ids >= 256, plus ours.
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 0, abilityId: 300));    // byte0 bit7
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));    // byte1 bit7 (ours)
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 15, abilityId: 400));   // byte1 bit0

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));

        Assert.Equal((byte)0x80, rec.Bytes[0]);   // slot 1's extend bit survives
        Assert.Equal((byte)0x01, rec.Bytes[1]);   // slot 16's extend bit survives; slot 9's is gone
        Assert.Equal(Barrage.SlotByte(300), rec.Bytes[3 + 0]);
        Assert.Equal((byte)0, rec.Bytes[3 + 8]);
        Assert.Equal(Barrage.SlotByte(400), rec.Bytes[3 + 15]);
    }

    [Fact]
    public void ReleaseSlot_refuses_when_slot_holds_a_different_ability_id()   // T4
    {
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        rec.Bytes[3 + 8] = 55;   // some other ability entirely, no extend bit
        byte[] before = (byte[])rec.Bytes.Clone();

        Assert.False(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));
        Assert.Equal(before, rec.Bytes);
    }

    [Fact]
    public void ReleaseSlot_refuses_the_aurablast_trap_byte_matches_but_extend_bit_disagrees()
    {
        // 358 (Barrage) and 102 (Aurablast) share the low byte 102 -- distinguished ONLY by the
        // extend bit. A slot whose byte is 102 with the extend bit CLEAR is really Aurablast, and
        // must never be released as if it were our Barrage.
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        rec.Bytes[3 + 8] = 102;   // Aurablast's own byte, no extend bit
        byte[] before = (byte[])rec.Bytes.Clone();

        Assert.False(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 8, abilityId: 358));
        Assert.Equal(before, rec.Bytes);
    }

    [Fact]
    public void ReleaseSlot_refuses_an_already_empty_slot()
    {
        // SlotByte(256) == 0 (256 & 0xFF), the same as a truly empty slot's byte -- an id whose low
        // byte is 0 must not be able to "release" a slot that never held anything.
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;

        Assert.False(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 0, abilityId: 256));
        Assert.Equal(new byte[Barrage.RecSize], rec.Bytes);
    }

    [Fact]
    public void ReleaseSlot_ability_below_256_leaves_the_extend_bit_clear_throughout()   // T5 (low id)
    {
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 3, abilityId: 165));   // Shadow Blade, < 256
        Assert.Equal((byte)0x00, rec.Bytes[0]);   // never set

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 3, abilityId: 165));
        Assert.Equal((byte)0, rec.Bytes[3 + 3]);
        Assert.Equal((byte)0x00, rec.Bytes[0]);   // still clear
    }

    [Fact]
    public void ReleaseSlot_ability_at_or_above_256_clears_the_extend_bit_it_set()   // T5 (high id)
    {
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 3, abilityId: 358));
        Assert.Equal((byte)0x10, rec.Bytes[0]);   // slotIdx 3 -> byte0 bit4 (0x80 >> 3)

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 3, abilityId: 358));
        Assert.Equal((byte)0, rec.Bytes[3 + 3]);
        Assert.Equal((byte)0x00, rec.Bytes[0]);
    }

    [Fact]
    public void ReleaseSlot_all_or_nothing_when_the_flag_word_is_not_writable()
    {
        // Readable+Writable slot byte, but the 2-byte extend-flag word is only READABLE, not
        // writable. The old shape (write the byte, then bail on the flag guard) would leave the
        // slot byte-cleared but the extend bit still set -- a phantom FindEmptySlot never reclaims.
        var m = new FakeSparseMemory();
        long flagAddr = 0x2000, abBase = 0x3000;   // slotIdx 0 -> byteAddr == abBase
        m.U8s[abBase] = Barrage.SlotByte(358);
        m.U8s[flagAddr] = 0x80;      // slotIdx 0's extend bit set -- matches "ours" for id 358
        m.ReadableAddrs.Add(abBase);
        m.ReadableAddrs.Add(flagAddr);
        m.WritableAddrs.Add(abBase);   // the ability byte IS writable...
        // ...but flagAddr is NOT in WritableAddrs -- the guard must refuse before touching anything.

        Assert.False(Barrage.ReleaseSlot(m, flagAddr, abBase, slotIdx: 0, abilityId: 358));
        Assert.False(m.Written.ContainsKey(abBase));
        Assert.Equal(Barrage.SlotByte(358), m.U8s[abBase]);   // untouched
    }

    [Fact]
    public void ReleaseSlot_two_grants_sharing_one_record_survive_each_others_release()   // T6
    {
        // The load-bearing non-vacuity proof for the primitive itself: Barrage (358) and a stand-in
        // second grant (Shadow Blade, 165) land in the SAME record at different slots -- exactly the
        // Sanguine Sword + Defender collision the plan describes. Releasing one must leave the
        // other completely intact, and releasing both must return the record to byte-identical
        // pre-injection state with neither resurrected. (The module-level version of this same
        // scenario, driving the real Barrage/ShadowBlade classes, lives in
        // BarrageShadowBladeCollisionTests -- this is the primitive-level half of the proof.)
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;

        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 0, abilityId: 358));    // "Barrage"
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 1, abilityId: 165));    // "Shadow Blade"

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 0, abilityId: 358));
        // Shadow Blade's slot is completely untouched by Barrage's release.
        Assert.Equal((byte)165, rec.Bytes[3 + 1]);
        Assert.Equal((byte)0, rec.Bytes[3 + 0]);

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 1, abilityId: 165));
        // Byte-identical to the record before either grant ever touched it -- and Barrage was NOT
        // resurrected by Shadow Blade's release.
        Assert.Equal(new byte[Barrage.RecSize], rec.Bytes);
    }

    [Fact]
    public void ReleaseSlot_a_learned_ability_in_another_slot_survives_our_release()   // T7
    {
        // Simulates the player learning a real ability into the record AFTER our inject (a raw
        // write into another empty slot, standing in for the game's own purchase writeback).
        using var rec = PinnedBuf.Of(Barrage.RecSize);
        long flagAddr = rec.Addr, abBase = rec.Addr + 3;
        Assert.True(Barrage.InjectSlot(Live, flagAddr, abBase, slotIdx: 0, abilityId: 358));

        rec.Bytes[3 + 5] = 40;   // player learns some real ability (id 40, < 256) into slot 6

        Assert.True(Barrage.ReleaseSlot(Live, flagAddr, abBase, slotIdx: 0, abilityId: 358));
        Assert.Equal((byte)40, rec.Bytes[3 + 5]);   // untouched by our release
    }

    // ---- (9) JobCommand table base re-anchor tripwire (the most dangerous WRITE in the port) ----
    // AbilityBase points at record 0's AbilityId1 in the live JobCommand table; ShadowBlade reuses
    // it verbatim. A stale-but-valid address here corrupts a REAL command list, so the verified
    // literal is pinned independently of the const -- an unintended edit trips this test (the same
    // tripwire shape ScholarRingTests uses for InventoryCountBase). 1.5 value re-found live
    // 2026-06-17 by tools/probes/jobcommand_find_probe.py: signature = rec 8 Aim bytes 150-157 +
    // rec 9 Martial Arts bytes 100-107, exactly 25 bytes apart; the WHOLE table then read coherently
    // at this base (Steal rec 14 = 108-115, Iaido rec 19, Machinist rec 37 = 213-215). Unique hit.
    // Delta +0x5080 from the pre-1.5 base 0x140679193 (non-monotonic gradient -- found, not predicted).
    [Fact]
    public void AbilityBase_is_pinned_to_the_verified_1_5_table_base()
        => Assert.Equal(0x14067E213L, Barrage.AbilityBase);
}
