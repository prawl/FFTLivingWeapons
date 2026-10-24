using System;
using System.Linq;
using Reloaded.Hooks.Definitions;
using Xunit;

namespace LivingWeapon.Tests;

public class HookCleanupTests
{
    private delegate int TestFn(int x);

    private static int Detour(int x) => x;

    private static FiringHooks.IFakeHook ActivatedHook(out IHook<TestFn> hook)
    {
        var fh = FiringHooks.Make(out var hooks);
        fh.Fire = false;
        hook = hooks.CreateHook<TestFn>(Detour, 0x1000);
        hook.Activate();
        return fh.Created[0];
    }

    [Fact]
    public void An_enabled_hook_is_disabled()
    {
        var fake = ActivatedHook(out var hook);
        Assert.True(fake.IsHookEnabled);
        HookCleanup.DisableQuietly(hook, LogVerb.Display, "thing");
        Assert.False(fake.IsHookEnabled);
        Assert.Equal(1, fake.DisableCalls);
    }

    [Fact]
    public void A_hook_that_is_not_enabled_is_left_alone()
    {
        var fh = FiringHooks.Make(out var hooks);
        var hook = hooks.CreateHook<TestFn>(Detour, 0x1000);
        HookCleanup.DisableQuietly(hook, LogVerb.Display, "thing");
        Assert.Equal(0, fh.Created[0].DisableCalls);
    }

    [Fact]
    public void A_null_hook_is_a_no_op()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        HookCleanup.DisableQuietly<TestFn>(null, LogVerb.Display, "thing");
        Assert.Empty(cap.File);
    }

    [Fact]
    public void A_throwing_disable_is_swallowed_and_logs_one_debug_line()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var ex = Record.Exception(() => HookCleanup.DisableQuietly(new ThrowingDisableHook(), LogVerb.Display, "thing"));
        Assert.Null(ex);
        var line = Assert.Single(cap.File);
        Assert.Contains("thing disable after a failed install swallowed: boom", line);
    }

    private sealed class ThrowingDisableHook : IHook<TestFn>
    {
        public TestFn OriginalFunction => Detour;
        public IReverseWrapper<TestFn> ReverseWrapper => throw new NotSupportedException();
        public bool IsHookEnabled => true;
        public bool IsHookActivated => true;
        public IntPtr OriginalFunctionAddress => IntPtr.Zero;
        public IntPtr OriginalFunctionWrapperAddress => IntPtr.Zero;
        public IHook<TestFn> Activate() => this;
        IHook IHook.Activate() => this;
        public void Disable() => throw new InvalidOperationException("boom");
        public void Enable() { }
    }
}
