using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-149 stage A: the OwnershipHold core shared by HoldUltima/HoldMushin/HoldAfterimage.
/// TryCapture is the sane-gate + NaturalLedger.FilterCapture consult; OwnsCurrentValue is the
/// three-token check (our last write, the untouched natural, or the recognized baked restart
/// residue); Step composes both (plus the Writable gate) into the branch a lane acts on.
///
/// NON-VACUITY TARGET: OwnsCurrentValue_BakedToken_OwnsWhenNothingElseMatches is the pin that
/// breaks if the baked token is dropped from the three-token check (LW-90's restart-residue
/// re-apply path would then read FOREIGN forever after a corrected capture).
/// </summary>
public class OwnershipHoldTests
{
    private const int NameId = 900;
    private const int Level = 30;
    private const long Addr = 0x9000_0000;

    private static FakeSparseMemory Mem(byte cur, bool writable = true)
    {
        var mem = new FakeSparseMemory();
        mem.U8s[Addr] = cur;
        if (writable) mem.WritableAddrs.Add(Addr);
        return mem;
    }

    // ---- TryCapture: sane-gate ----

    [Fact]
    public void TryCapture_accepts_a_sane_first_sight_as_natural()
    {
        var mem = Mem(15);
        var ledger = new NaturalLedger();
        bool ok = OwnershipHold.TryCapture(mem, ledger, Addr, StatLane.Pa, NameId, Level, out int natural, out int baked);
        Assert.True(ok);
        Assert.Equal(15, natural);
        Assert.Equal(0, baked);
    }

    [Fact]
    public void TryCapture_refuses_below_the_sane_range()
    {
        var mem = Mem(0);   // StatMin is 1
        var ledger = new NaturalLedger();
        bool ok = OwnershipHold.TryCapture(mem, ledger, Addr, StatLane.Pa, NameId, Level, out int natural, out int baked);
        Assert.False(ok);
        Assert.Equal(0, natural);
        Assert.Equal(0, baked);
    }

    [Fact]
    public void TryCapture_refuses_above_the_sane_range()
    {
        var mem = Mem(100);   // StatSaneHi is 99
        var ledger = new NaturalLedger();
        bool ok = OwnershipHold.TryCapture(mem, ledger, Addr, StatLane.Pa, NameId, Level, out int natural, out int baked);
        Assert.False(ok);
    }

    [Fact]
    public void TryCapture_consults_the_ledger_and_reports_a_corrected_residue()
    {
        // Non-vacuity vs a capture that blindly trusts firstSight: seed the ledger with the exact
        // restart shape NaturalLedgerTests pins (accept 8, record target 11, double reset, then a
        // first sight of 11 must correct to 8 and report 11 as the baked residue).
        var mem = Mem(11);
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Pa, 8, Level, out _);
        ledger.RecordWrite(NameId, StatLane.Pa, 11);
        ledger.OnBattleReset();
        ledger.OnBattleReset();

        bool ok = OwnershipHold.TryCapture(mem, ledger, Addr, StatLane.Pa, NameId, Level, out int natural, out int baked);
        Assert.True(ok);
        Assert.Equal(8, natural);
        Assert.Equal(11, baked);
    }

    // ---- OwnsCurrentValue: the three-token check ----

    [Fact]
    public void OwnsCurrentValue_LastTargetToken_Owns()
        => Assert.True(OwnershipHold.OwnsCurrentValue(cur: 20, lastTarget: 20, natural: 15, baked: 0));

    [Fact]
    public void OwnsCurrentValue_NaturalToken_Owns()
        => Assert.True(OwnershipHold.OwnsCurrentValue(cur: 15, lastTarget: 20, natural: 15, baked: 0));

    [Fact]
    public void OwnsCurrentValue_BakedToken_OwnsWhenNothingElseMatches()
    {
        // NON-VACUITY TARGET: drop "(baked > 0 && cur == baked)" from the check and this flips
        // from true to false -- the LW-90 post-restart re-apply path would go foreign forever.
        Assert.True(OwnershipHold.OwnsCurrentValue(cur: 30, lastTarget: 20, natural: 15, baked: 30));
    }

    [Fact]
    public void OwnsCurrentValue_BakedTokenInert_WhenBakedIsZero()
        // baked == 0 means "no residue on file"; a coincidental cur == 0 must NOT be treated as owned.
        => Assert.False(OwnershipHold.OwnsCurrentValue(cur: 0, lastTarget: 20, natural: 15, baked: 0));

    [Fact]
    public void OwnsCurrentValue_Foreign_WhenNoTokenMatches()
        => Assert.False(OwnershipHold.OwnsCurrentValue(cur: 99, lastTarget: 20, natural: 15, baked: 30));

    // ---- Step: the composed branch report ----

    [Fact]
    public void Step_Refused_WhenAddrNotWritable()
    {
        var mem = Mem(15, writable: false);
        var ledger = new NaturalLedger();
        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: false, lastTarget: 0, natural: 0, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Refused, result.Branch);
    }

    [Fact]
    public void Step_Refused_WhenNoRecordAndFirstSightOutOfRange()
    {
        var mem = Mem(0);
        var ledger = new NaturalLedger();
        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: false, lastTarget: 0, natural: 0, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Refused, result.Branch);
    }

    [Fact]
    public void Step_Captured_OnFirstSight()
    {
        var mem = Mem(12);
        var ledger = new NaturalLedger();
        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: false, lastTarget: 0, natural: 0, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Captured, result.Branch);
        Assert.Equal(12, result.Natural);
        Assert.Equal(0, result.Baked);
        Assert.Equal(12, result.Cur);   // nothing wrote in between the capture read and this report
    }

    [Fact]
    public void Step_Captured_ReportsTheCorrectedBaked_WhenRestartResidueIsSeen()
    {
        var mem = Mem(11);
        var ledger = new NaturalLedger();
        ledger.FilterCapture(NameId, StatLane.Pa, 8, Level, out _);
        ledger.RecordWrite(NameId, StatLane.Pa, 11);
        ledger.OnBattleReset();
        ledger.OnBattleReset();

        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: false, lastTarget: 0, natural: 0, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Captured, result.Branch);
        Assert.Equal(8, result.Natural);
        Assert.Equal(11, result.Baked);
    }

    [Fact]
    public void Step_Owned_WhenCurMatchesLastTarget()
    {
        var mem = Mem(20);
        var ledger = new NaturalLedger();
        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: true, lastTarget: 20, natural: 15, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Owned, result.Branch);
        Assert.Equal(20, result.Cur);
    }

    [Fact]
    public void Step_Foreign_WhenCurMatchesNoToken()
    {
        var mem = Mem(99);
        var ledger = new NaturalLedger();
        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: true, lastTarget: 20, natural: 15, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Foreign, result.Branch);
        Assert.Equal(99, result.Cur);
    }

    [Fact]
    public void Step_Refused_WhenRecordExistsButAddrNotWritable()
    {
        var mem = Mem(20, writable: false);
        var ledger = new NaturalLedger();
        var result = OwnershipHold.Step(mem, ledger, Addr, StatLane.Pa, NameId, Level,
                                        hasRecord: true, lastTarget: 20, natural: 15, baked: 0);
        Assert.Equal(OwnershipHold.Branch.Refused, result.Branch);
    }
}
