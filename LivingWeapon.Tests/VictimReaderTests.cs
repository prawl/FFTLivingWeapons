using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// VictimReader.Read is the shared guarded three-field victim read extracted from VictimProbe
/// (Reliquary Phase 0's P1 probe) so KillTracker.Corpses.cs's Phase 1 deed-capture stamp and
/// VictimProbe's log-only capture read identically -- no drift between the log-only probe and
/// the behavioral capture. These tests pin the guard parity (each field gated independently)
/// and the Has-with-job-0 semantics: job 0 is a VALID read outcome (classifies Unknown), not a
/// failure -- only an unreadable nameId makes Has false.
/// </summary>
public class VictimReaderTests
{
    private static long Addr => Band.Entry(0);

    [Fact]
    public void All_fields_readable_yields_a_full_snapshot()
    {
        var m = new FakeSparseMemory();
        long addr = Addr;
        m.U16s[addr + Offsets.ANameId] = 918;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        m.U8s[addr + Puppeteer.JobOff] = 77;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);
        m.U8s[addr + Offsets.ADeadStatus] = Offsets.AUndeadBit;
        m.ReadableAddrs.Add(addr + Offsets.ADeadStatus);

        var snap = VictimReader.Read(m, addr);

        Assert.True(snap.Has);
        Assert.Equal((ushort)918, snap.NameId);
        Assert.Equal((byte)77, snap.Job);
        Assert.True(snap.Undead);
    }

    [Fact]
    public void Unreadable_nameId_yields_Has_false_regardless_of_other_fields()
    {
        var m = new FakeSparseMemory();
        long addr = Addr;
        // nameId deliberately NOT marked readable.
        m.U8s[addr + Puppeteer.JobOff] = 77;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);
        m.U8s[addr + Offsets.ADeadStatus] = Offsets.AUndeadBit;
        m.ReadableAddrs.Add(addr + Offsets.ADeadStatus);

        var snap = VictimReader.Read(m, addr);

        Assert.False(snap.Has);
        Assert.Equal((ushort)0, snap.NameId);
    }

    [Fact]
    public void Unreadable_job_yields_zero_job_without_clearing_Has()
    {
        var m = new FakeSparseMemory();
        long addr = Addr;
        m.U16s[addr + Offsets.ANameId] = 918;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        // job deliberately NOT marked readable.

        var snap = VictimReader.Read(m, addr);

        Assert.True(snap.Has);   // Has gates on nameId only
        Assert.Equal((byte)0, snap.Job);
    }

    [Fact]
    public void Has_true_with_job_zero_is_a_valid_snapshot_not_a_failure()
    {
        // job==0 (Readable AND reads 0) is a real, storable classification outcome
        // (VictimClass.Classify lands it in Unknown) -- distinct from an unreadable job,
        // which also reads 0 but for a different reason. Both leave Has driven by nameId alone.
        var m = new FakeSparseMemory();
        long addr = Addr;
        m.U16s[addr + Offsets.ANameId] = 42;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        m.U8s[addr + Puppeteer.JobOff] = 0;
        m.ReadableAddrs.Add(addr + Puppeteer.JobOff);

        var snap = VictimReader.Read(m, addr);

        Assert.True(snap.Has);
        Assert.Equal((byte)0, snap.Job);
    }

    [Fact]
    public void Unreadable_undead_byte_yields_false_without_clearing_Has()
    {
        var m = new FakeSparseMemory();
        long addr = Addr;
        m.U16s[addr + Offsets.ANameId] = 918;
        m.ReadableAddrs.Add(addr + Offsets.ANameId);
        // undead status byte deliberately NOT marked readable.

        var snap = VictimReader.Read(m, addr);

        Assert.True(snap.Has);
        Assert.False(snap.Undead);
    }

    [Fact]
    public void Nothing_readable_yields_an_all_zero_default_snapshot_never_throws()
    {
        var m = new FakeSparseMemory();
        var ex = Record.Exception(() => VictimReader.Read(m, Addr));
        Assert.Null(ex);

        var snap = VictimReader.Read(m, Addr);
        Assert.False(snap.Has);
        Assert.Equal((ushort)0, snap.NameId);
        Assert.Equal((byte)0, snap.Job);
        Assert.False(snap.Undead);
    }
}
