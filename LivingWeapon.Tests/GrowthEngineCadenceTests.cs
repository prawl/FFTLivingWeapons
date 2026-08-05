using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-149 stage A cadence pins: how many times each hold calls NaturalLedger.RecordWrite per
/// evaluated tick, in the OWNED and FOREIGN cases, exactly as today's code behaves. Written and
/// run GREEN against the pre-extraction GrowthEngine.Ultima.cs/Mushin.cs/Afterimage.cs/cs (Hold)
/// FIRST (the adversarial plan review's own demand: the cadence numbers must be MEASURED, not
/// assumed), then re-run unchanged after the OwnershipHold extraction to prove the refactor left
/// the write cadence byte-identical. Counted via NaturalLedger.RecordWriteCalls (a total-
/// invocation counter added purely for this pin -- see NaturalLedger.cs) rather than the
/// ledger's recorded-target state, because the finding is about CALL COUNT, not which targets
/// got remembered.
///
/// Ultima/Mushin/Afterimage share one shape: capture-tick and the following owned tick each cost
/// exactly ONE RecordWrite call; a foreign tick (a real buff/debuff landed on the byte) costs
/// ZERO -- the call sits inside the ownership-confirmed branch only. Hold (the plain
/// multiplicative-factor lane, GrowthEngine.cs) does NOT share this shape: it calls RecordWrite
/// UNCONDITIONALLY on every evaluated tick once a record exists (recording the STALE previous
/// target, before the ownership check even runs), so an owned tick and a foreign tick cost the
/// SAME one call each. That mismatch is why Hold stays hand-rolled instead of riding the shared
/// OwnershipHold core (see GrowthEngine.OwnershipHold.cs's class doc).
/// </summary>
public class GrowthEngineCadenceTests
{
    private const int NameId = 700;
    private const long StructBase = 0x7000_0000;

    private static (GrowthEngine engine, FakeSparseMemory mem, NaturalLedger ledger) Build()
    {
        var mem = new FakeSparseMemory();
        var ledger = new NaturalLedger();
        var engine = new GrowthEngine(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(),
                                      new TurnTracker(mem), mem, null, ledger);
        return (engine, mem, ledger);
    }

    // ---- Ultima: owns PA, reads HP every tick ----

    [Fact]
    public void Ultima_RecordWrite_cadence_capture_owned_then_foreign()
    {
        var (engine, mem, ledger) = Build();
        var m = new WeaponMeta
        {
            Name = "Materia Blade", Wp = 10, Cat = "Sword", Formula = 1, Flavor = "ultima blade",
            Signature = new WeaponSignature { AtTier = 3, Ultima = true, DisplayLabel = "Ultima" }
        };
        long addr = StructBase + Offsets.CPa;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;
        MemSeats.SeatBand(mem, 4, weapon: 77, lvl: 30, br: 65, fa: 60, gx: 3, gy: 3, hp: 300, maxHp: 300);
        mem.ReadableAddrs.Add(Band.Entry(4) + Offsets.AMaxHp);

        engine.HoldUltima(StructBase, m, tier: 0, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(1, ledger.RecordWriteCalls);   // capture tick: trivially owned (nothing wrote in between), records once

        engine.HoldUltima(StructBase, m, tier: 0, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(2, ledger.RecordWriteCalls);   // owned tick: +1

        mem.U8s[addr] = 250;   // a foreign value: not lastTarget, not natural, baked==0 so the baked clause is inert
        engine.HoldUltima(StructBase, m, tier: 0, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(2, ledger.RecordWriteCalls);   // foreign tick: +0
    }

    // ---- Mushin: owns PA, no HP read ----

    [Fact]
    public void Mushin_RecordWrite_cadence_capture_owned_then_foreign()
    {
        var (engine, mem, ledger) = Build();
        var m = new WeaponMeta
        {
            Name = "Kiku-ichimonji", Wp = 10, Cat = "Katana", Formula = 1, Flavor = "stillness blade",
            Signature = new WeaponSignature { AtTier = 3, Mushin = true, DisplayLabel = "Mushin" }
        };
        long addr = StructBase + Offsets.CPa;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        engine.HoldMushin(StructBase, m, tier: 3, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(1, ledger.RecordWriteCalls);

        engine.HoldMushin(StructBase, m, tier: 3, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(2, ledger.RecordWriteCalls);

        mem.U8s[addr] = 250;
        engine.HoldMushin(StructBase, m, tier: 3, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(2, ledger.RecordWriteCalls);
    }

    // ---- Afterimage: owns Speed; ramp dormant below +3 (also no HP read while dormant) ----

    [Fact]
    public void Afterimage_RecordWrite_cadence_capture_owned_then_foreign()
    {
        var (engine, mem, ledger) = Build();
        var m = new WeaponMeta
        {
            Name = "Swiftedge", Wp = 10, Cat = "Sword", Formula = 99, Flavor = "afterimage blade",
            Signature = new WeaponSignature { AtTier = 3, Afterimage = true, DisplayLabel = "Afterimage" }
        };
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        engine.HoldAfterimage(StructBase, m, tier: 1, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(1, ledger.RecordWriteCalls);

        engine.HoldAfterimage(StructBase, m, tier: 1, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        Assert.Equal(2, ledger.RecordWriteCalls);

        mem.U8s[addr] = 250;
        engine.HoldAfterimage(StructBase, m, tier: 1, level: 30, brave: 65, faith: 60, rosterNameId: NameId);
        // FOREIGN: the state dict still advances (AfterimageState) even though no RecordWrite
        // fires -- exactly why the OwnershipHold core reports the branch rather than owning the
        // dictionary itself (Afterimage is the one lane that must act on the Foreign branch too).
        Assert.Equal(2, ledger.RecordWriteCalls);
    }

    // ---- Hold / WriteTarget (the plain multiplicative lane): NOT converted; numbers explain why ----

    [Fact]
    public void Hold_RecordWrite_cadence_owned_and_foreign_cost_the_same_one_call()
    {
        var (engine, mem, ledger) = Build();
        long addr = StructBase + Offsets.CSpeed;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 10;

        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);   // capture tick: WriteTarget records once
        Assert.Equal(1, ledger.RecordWriteCalls);

        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);   // owned tick, factor unchanged: +1 (the stale-target call)
        Assert.Equal(2, ledger.RecordWriteCalls);

        mem.U8s[addr] = 250;   // a foreign value
        engine.Hold(addr, 0.15, StatLane.Speed, NameId, 30);   // foreign tick: STILL +1 (the same stale-target call fires
                                                                // BEFORE the ownership check even looks at cur==250)
        Assert.Equal(3, ledger.RecordWriteCalls);
    }
}
