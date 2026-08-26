using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-317 stage 2: the two new hold kinds Routes' multi-lane tokens dispatch to
/// (GrowthEngine.Lanes.cs) -- HoldFlatCapped (the "pa+ma+brave" katana Brave hold and the
/// "wp+faith" magic-gun Faith hold) and HoldU16 (the "hp" Knight Sword MaxHp hold). Driven
/// against FakeSparseMemory directly (the GrowthEngineTests.cs precedent for hold-machinery
/// tests, e.g. OwnershipHoldTests), never through Apply/roster plumbing -- these are unit tests
/// of the hold methods themselves.
/// </summary>
public class GrowthEngineLanesTests
{
    private static GrowthEngine NewEngine(FakeSparseMemory mem)
        => new(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), new TurnTracker(mem), mem);

    // ================= HoldFlatCapped (test items 5-7) =================

    [Fact]
    public void HoldFlatCapped_WritesCurrentNeverOrig()
    {
        var mem = new FakeSparseMemory();
        long s = 0x5000;
        long braveAddr = s + Offsets.CBraveCurrent;      // 0x2B
        long origBraveAddr = s + Offsets.CBrave;         // 0x2A
        long faithAddr = s + Offsets.CFaithCurrent;      // 0x2D
        long origFaithAddr = s + Offsets.CFaith;         // 0x2C
        mem.MarkWritable(braveAddr, 1);
        mem.MarkWritable(faithAddr, 1);
        mem.U8s[braveAddr] = 80;
        mem.U8s[faithAddr] = 40;
        mem.U8s[origBraveAddr] = 111;   // must never be touched
        mem.U8s[origFaithAddr] = 222;   // must never be touched

        var engine = NewEngine(mem);
        engine.HoldFlatCapped(braveAddr, flat: 12, cap: Tuning.BraveLaneCap, StatLane.Brave, nameId: 1, level: 10);
        engine.HoldFlatCapped(faithAddr, flat: 8, cap: Tuning.FaithLaneCap, StatLane.Faith, nameId: 1, level: 10);

        Assert.Equal((byte)92, mem.U8s[braveAddr]);   // 80 + 12
        Assert.Equal((byte)48, mem.U8s[faithAddr]);   // 40 + 8
        Assert.Equal((byte)111, mem.U8s[origBraveAddr]);
        Assert.Equal((byte)222, mem.U8s[origFaithAddr]);
        Assert.Contains(braveAddr, mem.WriteOrder);
        Assert.Contains(faithAddr, mem.WriteOrder);
        Assert.DoesNotContain(origBraveAddr, mem.WriteOrder);
        Assert.DoesNotContain(origFaithAddr, mem.WriteOrder);
    }

    [Fact]
    public void HoldFlatCapped_CapNeverLowers()
    {
        // nat 92 + 12 -> 97 (the cap bites, but is still above natural).
        var mem = new FakeSparseMemory();
        long addr = 0x6000 + Offsets.CBraveCurrent;
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 92;
        var engine = NewEngine(mem);
        engine.HoldFlatCapped(addr, flat: 12, cap: Tuning.BraveLaneCap, StatLane.Brave, nameId: 1, level: 10);
        Assert.Equal((byte)97, mem.U8s[addr]);

        // nat 98 -> the flat+cap would compute 97, BELOW natural -- max() keeps 98, no write.
        var mem2 = new FakeSparseMemory();
        long addr2 = 0x6100 + Offsets.CBraveCurrent;
        mem2.MarkWritable(addr2, 1);
        mem2.U8s[addr2] = 98;
        var engine2 = NewEngine(mem2);
        engine2.HoldFlatCapped(addr2, flat: 12, cap: Tuning.BraveLaneCap, StatLane.Brave, nameId: 1, level: 10);
        Assert.Equal((byte)98, mem2.U8s[addr2]);
        Assert.Empty(mem2.WriteOrder);   // target == natural via the max() -- no write needed
    }

    [Fact]
    public void HoldFlatCapped_KobuRaiseAboveTarget_LeftAlone()
    {
        var mem = new FakeSparseMemory();
        long addr = 0x7000 + Offsets.CBraveCurrent;
        mem.MarkWritable(addr, 1);
        mem.U8s[addr] = 80;
        var engine = NewEngine(mem);
        engine.HoldFlatCapped(addr, flat: 12, cap: Tuning.BraveLaneCap, StatLane.Brave, nameId: 1, level: 10);
        Assert.Equal((byte)92, mem.U8s[addr]);   // captured natural 80, held at 92

        // Kobu raised the byte mid-battle to a value that is neither natural nor our own target.
        mem.U8s[addr] = 71;
        mem.WriteOrder.Clear();
        engine.HoldFlatCapped(addr, flat: 12, cap: Tuning.BraveLaneCap, StatLane.Brave, nameId: 1, level: 10);
        Assert.Equal((byte)71, mem.U8s[addr]);   // left alone -- the Kobu compose rule
        Assert.Empty(mem.WriteOrder);
    }

    // ================= HoldU16 (test items 8-9, 16) =================

    [Fact]
    public void HoldU16_MaxHp_FactorAndCeiling()
    {
        var mem = new FakeSparseMemory();
        long addr = 0x8000 + Offsets.CMaxHp;
        mem.MarkWritable(addr, 2);
        mem.U16s[addr] = 700;
        var engine = NewEngine(mem);
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)910, mem.U16s[addr]);   // 700 * 1.30 = 910

        var mem2 = new FakeSparseMemory();
        long addr2 = 0x8100 + Offsets.CMaxHp;
        mem2.MarkWritable(addr2, 2);
        mem2.U16s[addr2] = 770;
        var engine2 = NewEngine(mem2);
        engine2.HoldU16(addr2, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)999, mem2.U16s[addr2]);   // 770 * 1.30 = 1001 -> clamp bites

        var mem3 = new FakeSparseMemory();
        long addr3 = 0x8200 + Offsets.CMaxHp;
        mem3.MarkWritable(addr3, 2);
        mem3.U16s[addr3] = 500;
        var engine3 = NewEngine(mem3);
        engine3.HoldU16(addr3, Tuning.Factor[2], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)600, mem3.U16s[addr3]);   // 500 * 1.20 = 600

        // Re-apply on natural: the engine's per-turn normalize resets the byte back to 700.
        mem.U16s[addr] = 700;
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)910, mem.U16s[addr]);

        // Leave 823 alone: neither natural (700) nor our own target (910).
        mem.U16s[addr] = 823;
        mem.WrittenU16.Clear();
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)823, mem.U16s[addr]);
        Assert.Empty(mem.WrittenU16);
    }

    [Fact]
    public void HoldU16_SaneCaptureRange()
    {
        var mem = new FakeSparseMemory();
        long addr = 0x9000 + Offsets.CMaxHp;
        mem.MarkWritable(addr, 2);
        mem.U16s[addr] = 1600;   // above the 1..1500 sane capture range
        var engine = NewEngine(mem);
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)1600, mem.U16s[addr]);
        Assert.Empty(mem.WrittenU16);

        var mem2 = new FakeSparseMemory();
        long addr2 = 0x9100 + Offsets.CMaxHp;
        mem2.MarkWritable(addr2, 2);
        mem2.U16s[addr2] = 0;
        var engine2 = NewEngine(mem2);
        engine2.HoldU16(addr2, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)0, mem2.U16s[addr2]);
        Assert.Empty(mem2.WrittenU16);
    }

    [Fact]
    public void HoldU16_RestartResidue_FilteredByWriteRecord()
    {
        // Decision 3 (LW-317 plan) accepted a restart-residue compounding consequence for HoldU16
        // sans NaturalLedger, on the theory that it was battle-scoped and self-correcting. That
        // acceptance was OVERTURNED 2026-08-26 (LW-330): owner-witnessed live play showed the
        // pre-battle transition firing SEVERAL full enter/exit cycles before the real fight; each
        // exit cleared _appliedU16 while the WRITTEN max persisted in game memory across them, so
        // the next capture read the prior target as natural and compounded toward the ceiling
        // (697 -> 906 -> 999, with a phantom +93 top-up heal riding along). The reset-surviving
        // _u16WriteRecord now filters a recapture of the last recorded target back to the true
        // natural, so the hold no longer creeps.
        var mem = new FakeSparseMemory();
        long addr = 0xA000 + Offsets.CMaxHp;
        mem.MarkWritable(addr, 2);
        mem.U16s[addr] = 700;
        var engine = NewEngine(mem);
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)910, mem.U16s[addr]);   // 700 * 1.30 = 910

        engine.ResetBattle();   // battle restart: _appliedU16 clears; the write record survives
        // The engine's own restart rebuild carries the mod's held byte (910) forward instead of
        // the true natural (700) -- the write record recognizes 910 as its own last target and
        // filters it back to natural 700, so the hold re-targets the SAME 910, not 999.
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)910, mem.U16s[addr]);   // filtered: held at the SAME target, no creep
    }

    [Fact]
    public void HoldU16_ResetRecapture_DoesNotCompound()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xC000;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // 697 * 1.30 = 906.1 -> 906
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);    // topped up 697 -> 906

        engine.ResetBattle();   // _appliedU16 clears; the write record survives -- that is the point
        // mem left at 906: the battle-enter sequence's several enter/exit resets leave the
        // WRITTEN max in game memory across them (owner-witnessed 2026-08-26).
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // filtered back to natural 697 -- NOT 999
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);    // no second top-up
    }

    [Fact]
    public void HoldU16_ResetRecapture_NeverPhantomHeals()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xC100;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);

        engine.ResetBattle();
        mem.U16s[hpAddr] = 700;   // damaged since the last hold
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // re-held at the same target
        Assert.Equal((ushort)700, mem.U16s[hpAddr]);    // no phantom heal -- damage stays exactly as it was
    }

    [Fact]
    public void HoldU16_FreshNaturalAfterReset_TopsUpAgain()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xC200;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);

        engine.ResetBattle();
        // A REAL new battle: the engine rebuilt the combat struct fresh from the roster's true
        // stats, so both max and current HP read the true natural again, not the held residue.
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // genuine capture, same target as before
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);    // top-up fires again -- once per REAL battle
    }

    [Fact]
    public void HoldU16_RetargetUpdatesWriteRecord()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xC300;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        var engine = NewEngine(mem);

        // Capture at a lower tier first.
        engine.HoldU16(maxAddr, Tuning.Factor[2], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)836, mem.U16s[maxAddr]);   // 697 * 1.20 = 836.4 -> 836

        // Mid-battle tier-up: cur == the just-written target (836), factor changed -- the retarget
        // branch fires (still the SAME battle; _appliedU16 still holds this address). The retarget
        // site must ALSO update the write record, not just the capture site.
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // 697 * 1.30 = 906.1 -> 906, from the SAME natural

        engine.ResetBattle();
        // mem left at 906 (the RETARGETED value) -- a recapture of THIS target must also be
        // filtered, which only works if the retarget branch recorded it.
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // filtered -- NOT round(906*1.30)=1178->999
    }

    [Fact]
    public void HoldU16_WriteRecord_KeyedPerUnit()
    {
        var mem = new FakeSparseMemory();
        long addr1 = 0xC400 + Offsets.CMaxHp;
        long hpAddr1 = 0xC400 + Offsets.CHp;
        long addr2 = 0xC500 + Offsets.CMaxHp;
        long hpAddr2 = 0xC500 + Offsets.CHp;
        mem.MarkWritable(addr1, 2);
        mem.MarkWritable(hpAddr1, 2);
        mem.MarkWritable(addr2, 2);
        mem.MarkWritable(hpAddr2, 2);
        mem.U16s[addr1] = 697;
        mem.U16s[hpAddr1] = 697;
        mem.U16s[addr2] = 906;   // deliberately equal to unit 1's about-to-be-recorded target
        mem.U16s[hpAddr2] = 906;
        var engine = NewEngine(mem);

        // Unit 1 (nameId 1): capture records (nameId 1, MaxHp) -> (natural 697, target 906).
        engine.HoldU16(addr1, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[addr1]);

        // Unit 2 (nameId 2, a DIFFERENT unit, never captured before): its own current max just
        // happens to equal unit 1's recorded target. The (nameId, lane) key means this is NOT
        // filtered -- it is a genuine first capture for unit 2 and compounds like any first
        // capture at that starting value would.
        engine.HoldU16(addr2, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 2, level: 10);
        Assert.Equal((ushort)999, mem.U16s[addr2]);    // round(906*1.30)=1178 -> clamped 999, NOT filtered
        Assert.Equal((ushort)999, mem.U16s[hpAddr2]);  // top-up DID fire -- treated as a genuine capture
    }

    [Fact]
    public void HoldU16_NameIdZero_FilterDisabled()
    {
        // No roster identity to key the write record on: the filter is disabled and the
        // pre-LW-330 compounding behavior is preserved exactly (accepted -- nameId <= 0 means
        // there is nothing to key the record on, matching decision 3's original acceptance for
        // the identity-less case).
        var mem = new FakeSparseMemory();
        long addr = 0xC600 + Offsets.CMaxHp;
        mem.MarkWritable(addr, 2);
        mem.U16s[addr] = 700;
        var engine = NewEngine(mem);

        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 0, level: 10);
        Assert.Equal((ushort)910, mem.U16s[addr]);   // 700 * 1.30 = 910

        engine.ResetBattle();
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 0, level: 10);
        Assert.Equal((ushort)999, mem.U16s[addr]);   // 910 * 1.30 = 1183 -> the ceiling still bites; compounds
    }

    // ================= HoldU16 current-HP top-up (LW-327) =================
    // At the FIRST CAPTURE only, HoldU16 also raises current HP by the same delta the max rose,
    // so a knight opens the battle reading the grown HP instead of hurt (679/883, not 679/883
    // held-but-679-shown). Never on the per-turn re-apply or retarget branches, never past the
    // new target, never on a KO'd unit.

    [Fact]
    public void HoldU16_FirstCapture_TopsUpCurrentHp()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB000;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 679;   // full HP
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)883, mem.U16s[maxAddr]);   // 679 * 1.30 = 882.7 -> 883
        Assert.Equal((ushort)883, mem.U16s[hpAddr]);    // opens at the full grown HP, not hurt
    }

    [Fact]
    public void HoldU16_TopUp_PreservesCarriedDamage()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB100;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 579;   // carrying 100 real damage
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)883, mem.U16s[maxAddr]);
        Assert.Equal((ushort)783, mem.U16s[hpAddr]);   // 883 - 100: the damage stays visible
    }

    [Fact]
    public void HoldU16_TopUp_NeverRepeats_OnReapplyOrRetarget()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB200;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 679;
        var engine = NewEngine(mem);

        // First capture: hp tops up to the new target (883).
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)883, mem.U16s[hpAddr]);

        // The engine's per-turn normalize resets max back to natural; the unit then takes a hit.
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 750;   // X, below target, carried forward from here on

        // Re-apply on natural (cur == e.natural), SAME factor: max is re-held, hp is untouched.
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)883, mem.U16s[maxAddr]);
        Assert.Equal((ushort)750, mem.U16s[hpAddr]);   // still X -- no repeat top-up

        // Retarget branch (cur == e.target, factor changed -- a kill-tier crossed mid-battle):
        // max moves again, hp is still never touched.
        engine.HoldU16(maxAddr, Tuning.Factor[2], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)750, mem.U16s[hpAddr]);   // still X
    }

    [Fact]
    public void HoldU16_TopUp_SkipsKOdUnit()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB300;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 0;   // KO'd
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)883, mem.U16s[maxAddr]);
        Assert.Equal((ushort)0, mem.U16s[hpAddr]);   // never revives a KO'd unit
        Assert.False(mem.WrittenU16.ContainsKey(hpAddr));
    }

    [Fact]
    public void HoldU16_TopUp_SkipsUnwritableHp()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB400;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);   // hpAddr deliberately left NOT writable
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 679;
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)883, mem.U16s[maxAddr]);
        Assert.Equal((ushort)679, mem.U16s[hpAddr]);   // unchanged
        Assert.False(mem.WrittenU16.ContainsKey(hpAddr));   // no write attempted
    }

    [Fact]
    public void HoldU16_TopUp_LeavesAnomalousHpAlone()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB500;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 729;   // above the old natural max -- anomalous, leave it
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)883, mem.U16s[maxAddr]);
        Assert.Equal((ushort)729, mem.U16s[hpAddr]);
        Assert.False(mem.WrittenU16.ContainsKey(hpAddr));
    }

    [Fact]
    public void HoldU16_TopUp_ZeroDelta_NoWrite()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xB600;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 679;
        mem.U16s[hpAddr] = 679;
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[0], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)679, mem.U16s[maxAddr]);   // 679 * 1.00 = 679 -- no growth at tier 0
        Assert.Equal((ushort)679, mem.U16s[hpAddr]);    // no delta, no top-up write
        Assert.False(mem.WrittenU16.ContainsKey(hpAddr));
    }

    // ================= HoldU16 top-up as a PENDING INTENT (LW-330 stage 2) =================
    // Tonight's live failure (owner-witnessed 2026-08-26): the pre-battle transition's capture
    // top-up worked, the game then tore the transient struct back to natural, and the REAL
    // battle's genuine capture ran at an instant when the freshly-built struct's current-HP
    // field was not yet populated (read < 1), so the old one-shot top-up's KO guard skipped it
    // and the heal never happened. The fix replaces the one-instant decision with a pending
    // intent that retries on every later HoldU16 call for that (nameId, lane) until hp reads a
    // sane value.

    [Fact]
    public void HoldU16_TopUp_DeliversWhenHpArrivesLate()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xD000;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 0;   // struct not yet populated by the game
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // 697 * 1.30 = 906.1 -> 906
        Assert.Equal((ushort)0, mem.U16s[hpAddr]);      // deferred, not dropped

        mem.U16s[hpAddr] = 697;   // the struct finishes populating a tick later
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);    // delivered on the retry
    }

    [Fact]
    public void HoldU16_TopUp_PendingSurvivesReset_DeliversAfterRebuild()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xD100;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 0;   // undelivered when the reset hits
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);
        Assert.Equal((ushort)0, mem.U16s[hpAddr]);

        engine.ResetBattle();   // _appliedU16 clears; the pending intent survives -- that is the point
        // The game rebuilds the REAL struct fresh: both fields read the true natural again. The
        // write record holds (697, 906), so this recapture of 697 is genuine, not held.
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);

        Assert.Equal((ushort)906, mem.U16s[maxAddr]);
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);    // the surviving intent delivers on the rebuild
    }

    [Fact]
    public void HoldU16_TopUp_HeldCaptureNeverCreatesPending()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xD200;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 697;
        var engine = NewEngine(mem);

        // Normal capture + immediate delivery: pending is created and removed same tick.
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);

        engine.ResetBattle();   // _appliedU16 clears; mem left at the held residue (906/906)
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);   // held/filtered, not a genuine recapture

        // A real hit lands. If the held recapture above had wrongly created a new pending
        // intent, the next call(s) would phantom-heal it right back up.
        mem.U16s[hpAddr] = 700;
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)700, mem.U16s[hpAddr]);
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)700, mem.U16s[hpAddr]);    // still no phantom heal
    }

    [Fact]
    public void HoldU16_TopUp_PendingSkipsAnomalousHp()
    {
        var mem = new FakeSparseMemory();
        long baseAddr = 0xD300;
        long maxAddr = baseAddr + Offsets.CMaxHp;
        long hpAddr = baseAddr + Offsets.CHp;
        mem.MarkWritable(maxAddr, 2);
        mem.MarkWritable(hpAddr, 2);
        mem.U16s[maxAddr] = 697;
        mem.U16s[hpAddr] = 0;   // undelivered
        var engine = NewEngine(mem);

        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[maxAddr]);
        Assert.Equal((ushort)0, mem.U16s[hpAddr]);

        // Something briefly pushes hp above the OLD natural -- anomalous, must not be trusted.
        mem.U16s[hpAddr] = 747;   // natural(697) + 50
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)747, mem.U16s[hpAddr]);    // left alone, still pending

        // hp settles back to the true natural -- now it delivers.
        mem.U16s[hpAddr] = 697;
        engine.HoldU16(maxAddr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)906, mem.U16s[hpAddr]);
    }
}
