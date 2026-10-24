using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the prompt hook's door reading. Arm(null) is safe in the refusal test because Arm returns
/// before it touches the hooks controller when the prologue read fails.
/// </summary>
public class PromptSwapHookDoorTests
{
    internal static PromptSwapHook NewHook(FakeSparseMemory mem)
    {
        var toast = new BannerToast(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), true);
        return new PromptSwapHook(new PromptSwap(toast, mem), mem);
    }

    [Fact]
    public void Door_waits_for_the_loader_before_anything_arrives()
    {
        var hook = NewHook(new FakeSparseMemory());

        Assert.Equal(DoorState.Waiting, hook.Door.State);
        Assert.Equal("the loader has not handed over its hooks yet", hook.Door.Reason);
        Assert.False(hook.Door.FailedCheck);
    }

    [Fact]
    public void A_moved_prologue_closes_the_door_as_a_failed_check()
    {
        using var cap = LogCapture.Start();
        var hook = NewHook(new FakeSparseMemory());   // no bytes staged at the hook target, so the prologue read fails

        hook.Arm(null!);

        Assert.Equal(DoorState.Closed, hook.Door.State);
        Assert.True(hook.Door.FailedCheck);
        Assert.Equal("the game's prompt system is not where this mod expects it", hook.Door.Reason);
        Assert.Contains(cap.File, l => l.Contains("[WARN]") && l.Contains("prompt code is not where this mod expects it"));
    }

    [Fact]
    public void No_hooks_closes_the_door_as_an_absence()
    {
        var hook = NewHook(new FakeSparseMemory());

        hook.MarkNoHooks();

        Assert.Equal(DoorState.Closed, hook.Door.State);
        Assert.False(hook.Door.FailedCheck);
        Assert.Equal("the loader gave no hooks", hook.Door.Reason);
    }

    private static readonly byte[] SetterPrologue = { 0x40, 0x55, 0x53, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x50 };

    private static PromptSwapHook Prologued()
    {
        var mem = new FakeSparseMemory();
        mem.TerrainBlocks[ImageBase.At(0x1403F109C)] = (byte[])SetterPrologue.Clone();
        return NewHook(mem);
    }

    [Fact]
    public void Arm_survives_a_detour_firing_during_activate()
    {
        using var cap = LogCapture.Start();
        var hook = Prologued();
        var fh = FiringHooks.Make(out var hooks);

        hook.Arm(hooks);

        Assert.True(fh.Created[0].Fired);
        Assert.Equal(DoorState.Open, hook.Door.State);
    }

    [Fact]
    public void Arm_after_a_failed_activate_leaves_no_stale_hook_and_retries_with_a_new_one()
    {
        using var cap = LogCapture.Start();
        var hook = Prologued();
        var fh = FiringHooks.Make(out var hooks);
        fh.ThrowOnActivation = 1;
        fh.EnabledWhenThrowing = true;

        hook.Arm(hooks);
        Assert.Equal(DoorState.Closed, hook.Door.State);
        Assert.False(fh.Created[0].IsHookEnabled);

        fh.ThrowOnActivation = 0;
        hook.Arm(hooks);
        Assert.Equal(2, fh.Created.Count);
        Assert.True(fh.Created[1].Activated);
        Assert.Equal(DoorState.Open, hook.Door.State);
    }

    private sealed class ThrowingInfoLogger : ILogger
    {
        public LogLevel LogLevel { get; set; }
        public void Log(LogVerb verb, string message) => throw new InvalidOperationException("log refused");
        public void LogWarning(LogVerb verb, string message) { }
        public void LogError(LogVerb verb, string message) { }
        public void LogError(LogVerb verb, string message, Exception exception) { }
        public void LogDebug(LogVerb verb, string message) { }
        public void NoteBattleEdge() { }
    }

    [Fact]
    public void Arm_keeps_an_activated_hook_when_a_later_step_throws()
    {
        var hook = Prologued();
        var fh = FiringHooks.Make(out var hooks);
        fh.Fire = false;
        var prior = ModLogger.Instance;
        ModLogger.Instance = new ThrowingInfoLogger();
        try
        {
            hook.Arm(hooks);
            hook.Arm(hooks);
        }
        finally { ModLogger.Instance = prior; }

        Assert.Single(fh.Created);
        Assert.True(fh.Created[0].IsHookEnabled);
        Assert.Equal(0, fh.Created[0].DisableCalls);
        Assert.Equal(DoorState.Closed, hook.Door.State);
        Assert.True(hook.Door.FailedCheck);
    }
}
