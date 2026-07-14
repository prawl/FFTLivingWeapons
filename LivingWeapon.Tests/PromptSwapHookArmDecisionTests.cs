using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PromptSwapHook.ShouldArm: the pure refusal decision extracted from Arm() so the landmark
/// guard is testable without touching IReloadedHooks (the native detour install itself stays
/// untestable, as today; see PromptSwapTests.cs's class doc). ShouldArm composes "did the
/// guarded read succeed" with HookLandmark.Verify's prefix-compare against ExpectedPrologue: the
/// 1.5.1 FnSetTextString incident (installing a detour on a stale address corrupted the function
/// and crashed the game) is exactly the case this refuses instead of hooking blind.
/// </summary>
public class PromptSwapHookArmDecisionTests
{
    // Mirrors PromptSwapHook.ExpectedPrologue (sub rsp,28h; mov rax,[rcx+10h]; mov r8b,dl).
    private static readonly byte[] Prologue = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC2 };

    [Fact]
    public void Read_ok_and_matching_prologue_arms()
    {
        Assert.True(PromptSwapHook.ShouldArm(readOk: true, Prologue));
    }

    [Fact]
    public void Read_ok_but_mismatched_prologue_refuses()
    {
        byte[] wrong = { 0x45, 0x84, 0xC0, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC2 };

        Assert.False(PromptSwapHook.ShouldArm(readOk: true, wrong));
    }

    [Fact]
    public void Failed_read_refuses_even_with_correct_bytes_present()
    {
        // A failed read is always a refusal, regardless of what happens to be sitting in the buffer.
        Assert.False(PromptSwapHook.ShouldArm(readOk: false, Prologue));
    }

    [Fact]
    public void Null_prologue_refuses()
    {
        Assert.False(PromptSwapHook.ShouldArm(readOk: true, null));
    }
}
