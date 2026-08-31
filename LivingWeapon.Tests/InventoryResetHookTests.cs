using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-351 fix round 7 (R7-1): the detour on the game's per-item inventory reset
/// (0x140284500). Since fix round 2 widened that routine's loop bound, every run of it zeroes
/// the extended ids' bag counts too, and the game refills only ids 0..260 from the save struct
/// afterwards (owner-observed 2026-08-30 23:29: every extended count read 0 after one real
/// battle). The hook keeps the extended bytes across the call. Process is the whole detour body
/// with a lambda standing in for the game's routine.</summary>
public class InventoryResetHookTests
{
    private const long Bag = Offsets.BagCountArray + ExtendedCatalog.FirstExtendedId;

    private static FakeCodePatcher Bagged(params byte[] counts)
    {
        var f = new FakeCodePatcher();
        f.Seed(Bag, counts);
        return f;
    }

    [Fact]
    public void Process_restores_the_extended_counts_a_zeroing_reset_wiped()
    {
        var f = Bagged(2, 0, 5);
        var hook = new InventoryResetHook(f, 3);
        hook.Process(2, _ => { f.TryWrite(Bag, new byte[] { 0, 0, 0 }); return 0; });
        Assert.Equal(new byte[] { 2, 0, 5 }, f.Read(Bag, 3));
        Assert.Equal(1, hook.Runs);
        Assert.Equal(1, hook.Restores);
    }

    [Fact]
    public void The_originals_return_value_passes_through_untouched()
    {
        // Round-7 verify (F6): no caller of 0x140284500 could be found on disk to prove eax is
        // never read, so the detour must hand rax back exactly as the game's routine left it.
        var f = Bagged(1);
        var hook = new InventoryResetHook(f, 1);
        Assert.Equal((nint)42, hook.Process(0, _ => 42));
    }

    [Fact]
    public void Process_writes_nothing_when_the_reset_left_the_counts_alone()
    {
        var f = Bagged(1, 4);
        var hook = new InventoryResetHook(f, 2);
        hook.Process(0, _ => 0);
        Assert.Empty(f.Writes);
        Assert.Equal(1, hook.Runs);
        Assert.Equal(0, hook.Restores);
    }

    [Fact]
    public void Process_passes_through_when_the_bag_is_unreadable_and_still_runs_the_original_once()
    {
        var blind = new FakeCodePatcher();   // nothing readable at all
        var hook = new InventoryResetHook(blind, 8);
        int calls = 0;
        hook.Process(0, _ => { calls++; return 0; });
        Assert.Equal(1, calls);
        Assert.Empty(blind.Writes);
    }

    [Fact]
    public void The_mode_reaches_the_original_unchanged()
    {
        var f = Bagged(3);
        var hook = new InventoryResetHook(f, 1);
        var seen = new System.Collections.Generic.List<int>();
        hook.Process(0, m => { seen.Add(m); return 0; });
        hook.Process(2, m => { seen.Add(m); return 0; });
        hook.Process(7, m => { seen.Add(m); return 0; });
        Assert.Equal(new[] { 0, 2, 7 }, seen);
        Assert.Equal(3, hook.Runs);
    }

    [Fact]
    public void ShouldArm_requires_the_live_prologue()
    {
        // 1.5.2 exe on disk, 0x140284500: mov [rsp+8],rbx; push rsi; sub rsp,20h; mov ebx,ecx
        var live = new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x8B, 0xD9, 0xE8, 0xC3 };
        Assert.True(InventoryResetHook.ShouldArm(true, live));
        var wrong = (byte[])live.Clone();
        wrong[11] = 0xD8;   // mov ebx,eax instead: a different routine
        Assert.False(InventoryResetHook.ShouldArm(true, wrong));
        Assert.False(InventoryResetHook.ShouldArm(false, InventoryResetHook.ExpectedPrologue));
        Assert.Equal(12, InventoryResetHook.ExpectedPrologue.Length);
        Assert.Equal(Offsets.FnInventoryReset, new InventoryResetHook(new FakeCodePatcher(), 1).TargetAddr);
        Assert.Equal(0x140284500L, Offsets.FnInventoryReset);
    }

    /// <summary>LW-368 round 2 (T11): the hook reads and restores through an injected base, not
    /// the vanilla constant.</summary>
    [Fact]
    public void Process_reads_and_restores_through_an_injected_bag_base()
    {
        const long altBase = 0x150000000L;
        long altBag = altBase + ExtendedCatalog.FirstExtendedId;
        var f = new FakeCodePatcher();
        f.Seed(altBag, 2, 0, 5);
        var hook = new InventoryResetHook(f, 3, bagBase: altBase);

        hook.Process(2, _ => { f.TryWrite(altBag, new byte[] { 0, 0, 0 }); return 0; });

        Assert.Equal(new byte[] { 2, 0, 5 }, f.Read(altBag, 3));
        Assert.Equal(1, hook.Restores);
        Assert.False(f.Bytes.ContainsKey(Offsets.BagCountArray + ExtendedCatalog.FirstExtendedId));   // never the vanilla block
    }
}
