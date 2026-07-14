using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PromptSwapHook.ShouldArm: the pure refusal decision extracted from Arm() so the landmark
/// guard is testable without touching IReloadedHooks (the native detour install itself stays
/// untestable, as today; see PromptSwapTests.cs's class doc). ShouldArm composes "did the
/// guarded read succeed" with HookLandmark.Verify's prefix-compare against ExpectedPrologue: the
/// 1.5.1 FnSetTextString incident (installing a detour on a stale address corrupted the function
/// and crashed the game) is exactly the case this refuses instead of hooking blind.
///
/// LW-89 step 2 (2026-07-14): FnSetTextString was retargeted from the 0x14028F750 dispatch
/// wrapper to the true text setter at 0x1403F1098, so Prologue below now mirrors the setter's
/// own prologue, not the wrapper's. WrapperPrologue keeps the wrapper's old bytes around as a
/// negative control: the wrapper is a real, readable prologue, just not this hook's target
/// anymore, so ShouldArm must refuse it exactly like any other mismatch.
/// </summary>
public class PromptSwapHookArmDecisionTests
{
    // Mirrors PromptSwapHook.ExpectedPrologue (push rbp; push rbx; push rdi; mov rbp,rsp;
    // sub rsp,50h), read live at the true setter 0x1403F1098 on 1.5.1, 2026-07-14 (LW-89).
    private static readonly byte[] Prologue = { 0x40, 0x55, 0x53, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x50 };

    // The superseded 0x14028F750 dispatch-wrapper prologue (sub rsp,28h; mov rax,[rcx+10h];
    // mov r8b,dl): a real, readable prologue, just the wrong function now. Negative control:
    // a landmark match against the OLD target must still refuse (LW-89 step 2).
    private static readonly byte[] WrapperPrologue = { 0x48, 0x83, 0xEC, 0x28, 0x48, 0x8B, 0x41, 0x10, 0x44, 0x8A, 0xC2 };

    [Fact]
    public void Read_ok_and_matching_prologue_arms()
    {
        Assert.True(PromptSwapHook.ShouldArm(readOk: true, Prologue));
    }

    [Fact]
    public void Read_ok_but_wrapper_prologue_refuses()
    {
        Assert.False(PromptSwapHook.ShouldArm(readOk: true, WrapperPrologue));
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
