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
    private static PromptSwapHook NewHook(FakeSparseMemory mem)
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
        Assert.Contains(cap.File, l => l.Contains("[WARN]") && l.Contains("does not match the expected prologue"));
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
}
