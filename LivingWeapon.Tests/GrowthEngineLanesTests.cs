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
    public void HoldU16_RestartResidue_DecisionPin()
    {
        // Decision 3 (LW-317 plan): HoldU16 does NOT thread the LW-90 NaturalLedger, so it has
        // no way to see through a battle restart carrying its own prior target forward as the
        // "natural" read. This pins the ACCEPTED consequence: the held value creeps upward
        // across a restart chain, but Tuning.HpCeiling still bites every time -- battle-scoped,
        // never unbounded.
        var mem = new FakeSparseMemory();
        long addr = 0xA000 + Offsets.CMaxHp;
        mem.MarkWritable(addr, 2);
        mem.U16s[addr] = 700;
        var engine = NewEngine(mem);
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)910, mem.U16s[addr]);   // 700 * 1.30 = 910

        engine.ResetBattle();   // battle restart: _appliedU16 is cleared, no ledger to consult
        // The engine's own restart rebuild carries the mod's held byte (910) forward instead of
        // the true natural (700) -- HoldU16 re-captures it as natural.
        engine.HoldU16(addr, Tuning.Factor[3], Tuning.HpCeiling, StatLane.MaxHp, nameId: 1, level: 10);
        Assert.Equal((ushort)999, mem.U16s[addr]);   // 910 * 1.30 = 1183 -> the ceiling still bites
    }
}
