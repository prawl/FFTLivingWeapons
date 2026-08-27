using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2: the transactional patch set and the post-load pending patch.</summary>
public class BytePatchSetTests
{
    private static readonly BytePatch A = new(0x140285E2D, 0x05, 0x06, "order-scan-guard");
    private static readonly BytePatch B = new(0x140286187, 0x06, 0x07, "acquired-walk");
    private static readonly BytePatch C = new(0x1403602F4, 0x05, 0x06, "battle-converter");

    private static FakeCodePatcher Vanilla()
    {
        var f = new FakeCodePatcher();
        f.Seed(A.Addr, A.Old); f.Seed(B.Addr, B.Old); f.Seed(C.Addr, C.Old);
        return f;
    }

    [Fact]
    public void Applies_every_site_in_order_and_rolls_back_newest_first()
    {
        var f = Vanilla();
        var set = new BytePatchSet();
        Assert.Null(set.Apply(f, new List<BytePatch> { A, B, C }));
        Assert.Equal(3, set.AppliedCount);
        Assert.Equal(new[] { A.Addr, B.Addr, C.Addr }, f.Writes.ConvertAll(w => w.Addr));
        Assert.Equal(A.New, f.Bytes[A.Addr]);
        Assert.Null(set.Apply(f, new List<BytePatch> { A, B, C }));   // idempotent: no second write pass
        Assert.Equal(3, f.Writes.Count);
        set.Rollback(f);
        Assert.Equal(0, set.AppliedCount);
        Assert.Equal(new[] { C.Addr, B.Addr, A.Addr }, f.Writes.GetRange(3, 3).ConvertAll(w => w.Addr));
        Assert.Equal(A.Old, f.Bytes[A.Addr]);
        Assert.Equal(C.Old, f.Bytes[C.Addr]);
    }

    [Fact]
    public void One_wrong_old_byte_refuses_the_whole_set_with_nothing_written()
    {
        var f = Vanilla();
        f.Seed(B.Addr, 0x07);   // already patched by something else (the research marker)
        var set = new BytePatchSet();
        var refusal = set.Apply(f, new List<BytePatch> { A, B, C });
        Assert.NotNull(refusal);
        Assert.Contains("acquired-walk", refusal);
        Assert.Contains("reads 07, expected 06", refusal);
        Assert.Empty(f.Writes);
        Assert.Equal(0, set.AppliedCount);
    }

    [Fact]
    public void An_unreadable_site_refuses_the_set()
    {
        var f = Vanilla();
        f.Bytes.Remove(C.Addr);
        var refusal = new BytePatchSet().Apply(f, new List<BytePatch> { A, B, C });
        Assert.Contains("unreadable", refusal);
        Assert.Empty(f.Writes);
    }

    [Fact]
    public void A_refused_write_rolls_back_what_already_landed()
    {
        var f = Vanilla();
        f.RefuseWritesAt.Add(C.Addr);
        var set = new BytePatchSet();
        var refusal = set.Apply(f, new List<BytePatch> { A, B, C });
        Assert.Contains("write refused", refusal);
        Assert.Contains("2 earlier patch(es) rolled back", refusal);
        Assert.Equal(0, set.AppliedCount);
        Assert.Equal(A.Old, f.Bytes[A.Addr]);
        Assert.Equal(B.Old, f.Bytes[B.Addr]);
        Assert.Equal(new[] { A.Addr, B.Addr, B.Addr, A.Addr }, f.Writes.ConvertAll(w => w.Addr));
    }

    [Fact]
    public void Pending_patch_waits_until_the_byte_reads_vanilla_then_writes_once()
    {
        var f = new FakeCodePatcher();
        var p = new PendingPatch(new BytePatch(0x14F2EA40F, 0x06, 0x07, "damage-hand-resolver"));
        Assert.False(p.Step(f));                 // unreadable: still waiting
        Assert.Equal(PendingPatch.Phase.Waiting, p.State);
        f.Seed(0x14F2EA40F, 0x06);
        Assert.True(p.Step(f));
        Assert.Equal(PendingPatch.Phase.Applied, p.State);
        Assert.Equal(0x07, f.Bytes[0x14F2EA40F]);
        Assert.False(p.Step(f));                 // settled: never steps again
        Assert.Single(f.Writes);
    }

    [Fact]
    public void Pending_patch_accepts_an_already_patched_byte_and_gives_up_on_a_foreign_one()
    {
        var already = new FakeCodePatcher();
        already.Seed(0x14F45D315, 0x5F);
        var p1 = new PendingPatch(new BytePatch(0x14F45D315, 0x5E, 0x5F, "damage-cap"));
        Assert.True(p1.Step(already));
        Assert.Equal(PendingPatch.Phase.AlreadyPatched, p1.State);
        Assert.Empty(already.Writes);

        var foreign = new FakeCodePatcher();
        foreign.Seed(0x14F45D315, 0x90);
        var p2 = new PendingPatch(new BytePatch(0x14F45D315, 0x5E, 0x5F, "damage-cap"));
        Assert.True(p2.Step(foreign));
        Assert.Equal(PendingPatch.Phase.Foreign, p2.State);
        Assert.Equal(0x90, p2.Observed);
        Assert.Empty(foreign.Writes);

        var locked = new FakeCodePatcher();
        locked.Seed(0x14F45D315, 0x5E);
        locked.RefuseWritesAt.Add(0x14F45D315);
        var p3 = new PendingPatch(new BytePatch(0x14F45D315, 0x5E, 0x5F, "damage-cap"));
        Assert.True(p3.Step(locked));
        Assert.Equal(PendingPatch.Phase.Unwritable, p3.State);
    }
}
