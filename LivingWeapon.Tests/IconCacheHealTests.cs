using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reloaded.Hooks.Definitions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>The heal on miss hook, driven through its test seam with a fake lookup and a fake loader.</summary>
public class IconCacheHealTests
{
    private static readonly (long Low, long High) Stack = (0x7000_0000L, 0x7001_0000L);
    private const long HeapKey = 0x2_0000_0000L;
    private const long NameAddr = 0x2_0000_1000L;
    private const long StackKey = 0x7000_0100L;
    private const long Manager = 0x3_0000_0000L;
    private const long Entry = 0xE000;
    private static readonly string Name = IconCacheHidePolicy.Needles(33)[0].Text;

    private static byte[] KeyBytes(long namePtr)
    {
        var b = new byte[16];
        BitConverter.GetBytes(namePtr).CopyTo(b, 0);
        BitConverter.GetBytes(0x100001u).CopyTo(b, 8);
        return b;
    }

    private static byte[] Padded(string s, int len = 256)
    {
        var b = new byte[len];
        Encoding.ASCII.GetBytes(s).CopyTo(b, 0);
        return b;
    }

    private sealed class Rig
    {
        public readonly FakeHeap Heap;
        public readonly IconCacheHeal Heal;
        public readonly List<(long Mgr, long Uri, byte Flag)> LoadCalls = new();
        public int OriginalCalls;
        public bool Loaded;
        public bool LoadSucceeds = true;
        public Action? OnLoad;

        public Rig(bool track = true, FakeHeap? heap = null)
        {
            Heap = heap ?? new FakeHeap(
                (HeapKey, KeyBytes(NameAddr)), (NameAddr, Padded(Name)), (StackKey, KeyBytes(NameAddr)));
            Heal = new IconCacheHeal(Heap, () => Stack);
            Heal.BindForTest(
                (m, k) => { Interlocked.Increment(ref OriginalCalls); return Loaded ? (nint)Entry : 0; },
                (m, uri, flag) =>
                {
                    lock (LoadCalls) LoadCalls.Add((m, uri, flag));
                    OnLoad?.Invoke();
                    if (LoadSucceeds) Loaded = true;
                    return (byte)(LoadSucceeds ? 1 : 0);
                });
            if (track) Heal.Track(new[] { Name });
        }

        public nint Look(long key = HeapKey, long mgr = Manager) => Heal.Detour((nint)mgr, (nint)key);
    }

    // ---- the pure decision ----

    [Theory]
    [InlineData(0x1000L, 120)]
    [InlineData(0x1FF0L, 16)]
    [InlineData(0x1FFFL, 1)]
    [InlineData(0x1F80L, 120)]
    public void FirstReadLength_StopsAtThePageEdge(long addr, int expected)
        => Assert.Equal(expected, IconCacheHealPolicy.FirstReadLength(addr));

    [Fact]
    public void NameFrom_JoinsTwoReadsAndRejectsAnUnterminatedName()
    {
        Assert.Equal("ab", IconCacheHealPolicy.NameFrom(new byte[] { (byte)'a', (byte)'b', 0, 9 }, null));
        Assert.Equal("abcd", IconCacheHealPolicy.NameFrom(new byte[] { (byte)'a', (byte)'b' }, new byte[] { (byte)'c', (byte)'d', 0 }));
        Assert.Null(IconCacheHealPolicy.NameFrom(new byte[] { (byte)'a', (byte)'b' }, null));
        Assert.Null(IconCacheHealPolicy.NameFrom(new byte[] { (byte)'a' }, new byte[] { (byte)'b' }));
        Assert.Null(IconCacheHealPolicy.NameFrom(new byte[] { 0 }, null));
    }

    // ---- the load bearing negative, through the detour ----

    [Fact]
    public void Detour_AStackKey_NeverReadsTheNameAndNeverCallsTheLoader()
    {
        var rig = new Rig();
        var result = rig.Look(StackKey);

        Assert.Equal(0, result);
        Assert.Empty(rig.LoadCalls);
        Assert.Equal(0, rig.Heap.ReadCalls);
        Assert.Equal(0, rig.Heal.Healed + rig.Heal.Refused);
    }

    // ---- the heal path ----

    [Fact]
    public void Detour_HealsATrackedMiss_ByCallingTheLoaderOnceWithFlagZeroAndTheKeysNamePointer()
    {
        var rig = new Rig();
        var result = rig.Look();

        Assert.Equal((nint)Entry, result);
        var call = Assert.Single(rig.LoadCalls);
        Assert.Equal((Manager, NameAddr, (byte)0), call);
        Assert.Equal(1, rig.Heal.Healed);
        Assert.Equal(0, rig.Heal.Refused);

        Assert.Equal((nint)Entry, rig.Look());   // the entry now exists, so no second load
        Assert.Single(rig.LoadCalls);
    }

    [Fact]
    public void Detour_ARefusedLoad_CountsRefusedAndReturnsTheMiss()
    {
        var rig = new Rig { LoadSucceeds = false };
        var result = rig.Look();

        Assert.Equal(0, result);
        Assert.Single(rig.LoadCalls);
        Assert.Equal(1, rig.Heal.Refused);
        Assert.Equal(0, rig.Heal.Healed);
    }

    [Fact]
    public void Detour_AnUntrackedNameOrNothingTracked_IsLeftAlone()
    {
        var other = new Rig(track: false);
        Assert.Equal(0, other.Look());
        Assert.Equal(0, other.Heap.ReadCalls);   // zero tracked returns before any read

        other.Heal.Track(new[] { "UITextureParts://ui/ffto/icon/equip_item/textureparts/ei_099_uitx" });
        Assert.Equal(0, other.Look());
        Assert.Empty(other.LoadCalls);
    }

    [Fact]
    public void Detour_AHit_PassesThroughWithoutAnyRead()
    {
        var rig = new Rig { Loaded = true };
        Assert.Equal((nint)Entry, rig.Look());
        Assert.Equal(0, rig.Heap.ReadCalls);
        Assert.Empty(rig.LoadCalls);
    }

    [Fact]
    public void Detour_TheLoadersOwnInnerLookup_PassesThroughWithoutLoadingAgain()
    {
        var rig = new Rig { LoadSucceeds = false };
        nint inner = -1;
        rig.OnLoad = () => inner = rig.Look();

        rig.Look();

        Assert.Equal(0, inner);
        Assert.Single(rig.LoadCalls);
    }

    // ---- the attempt cap ----

    [Fact]
    public void Detour_GivesUpAfterThreeAttempts_WithOneWarning()
    {
        var rig = new Rig { LoadSucceeds = false };
        using var cap = LogCapture.Start(LogLevel.Debug);

        for (int i = 0; i < 5; i++) Assert.Equal(0, rig.Look());

        Assert.Equal(3, rig.LoadCalls.Count);
        Assert.Equal(1, cap.File.Count(l => l.Contains("gave up")));
    }

    [Fact]
    public void Detour_SuccessfulHeals_NeverUseUpTheAttemptCap()
    {
        var rig = new Rig();
        for (int i = 0; i < 4; i++)
        {
            rig.Loaded = false;
            Assert.Equal((nint)Entry, rig.Look());
        }

        Assert.Equal(4, rig.LoadCalls.Count);
        Assert.Equal(0, rig.Heal.AttemptCount((nint)Manager, Name));
    }

    [Fact]
    public void Detour_ASuccessResetsTheConsecutiveFailures()
    {
        var rig = new Rig { LoadSucceeds = false };
        for (int i = 0; i < 2; i++) rig.Look();
        rig.LoadSucceeds = true;
        Assert.Equal((nint)Entry, rig.Look());
        rig.LoadSucceeds = false;
        rig.Loaded = false;
        for (int i = 0; i < 2; i++) rig.Look();
        rig.LoadSucceeds = true;
        rig.Loaded = false;

        Assert.Equal((nint)Entry, rig.Look());
        Assert.Equal(6, rig.LoadCalls.Count);
    }

    [Fact]
    public void Detour_CountsAttemptsPerManager()
    {
        var rig = new Rig { LoadSucceeds = false };
        for (int i = 0; i < 3; i++) rig.Look(mgr: Manager);
        rig.Look(mgr: Manager + 0x1000);

        Assert.Equal(4, rig.LoadCalls.Count);
    }

    // ---- tracking ----

    [Fact]
    public void Track_DedupesAndCapsAt242()
    {
        var heal = new IconCacheHeal(new FakeHeap(Array.Empty<(long, byte[])>()), () => Stack);
        heal.Track(new[] { "a", "a", "b" });
        Assert.Equal(2, heal.TrackedCount);

        heal.Track(Enumerable.Range(0, 400).Select(i => "n" + i));
        Assert.Equal(242, heal.TrackedCount);
    }

    private const long KnownEntry = 0xBEEF0;

    /// <summary>Answers only CreateHook, handing back a hook whose Activate fires the detour once.</summary>
    public class HooksProxy : DispatchProxy
    {
        internal FakeLookupHook? Created;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "CreateHook" || args?[0] is not IconCacheHeal.LookupFn detour)
                throw new NotSupportedException(targetMethod?.Name);
            return Created = new FakeLookupHook(detour);
        }
    }

    internal sealed class FakeLookupHook : IHook<IconCacheHeal.LookupFn>
    {
        private readonly IconCacheHeal.LookupFn _detour;
        public nint ActivateResult = -1;
        public FakeLookupHook(IconCacheHeal.LookupFn detour) => _detour = detour;

        public IconCacheHeal.LookupFn OriginalFunction => (m, k) => (nint)KnownEntry;
        public IReverseWrapper<IconCacheHeal.LookupFn> ReverseWrapper => throw new NotSupportedException();
        public bool IsHookEnabled => true;
        public bool IsHookActivated => true;
        public IntPtr OriginalFunctionAddress => IntPtr.Zero;
        public IntPtr OriginalFunctionWrapperAddress => IntPtr.Zero;
        public IHook<IconCacheHeal.LookupFn> Activate()
        {
            ActivateResult = _detour((nint)Manager, (nint)HeapKey);
            return this;
        }
        IHook IHook.Activate() => Activate();
        public void Disable() { }
        public void Enable() { }
    }

    [Fact]
    public void Arm_ADetourFiringDuringActivate_ForwardsThroughTheCapturedHookNotTheField()
    {
        long lookup = ImageBase.At(0x1403FBB80), load = ImageBase.At(0x1403FBE3C);
        var heap = new FakeHeap((lookup, Pad(IconCacheHeal.LookupPrologue)), (load, Pad(IconCacheHeal.LoadPrologue)));
        var heal = new IconCacheHeal(heap, () => Stack);
        heal.LoadBinder = _ => (m, u, f) => 1;
        var hooks = DispatchProxy.Create<IReloadedHooks, HooksProxy>();

        using var cap = LogCapture.Start(LogLevel.Debug);
        heal.Arm(hooks);

        Assert.True(((HooksProxy)(object)hooks).Created != null, string.Join('|', cap.File));
        var created = ((HooksProxy)(object)hooks).Created!;
        Assert.Equal((nint)KnownEntry, created.ActivateResult);
        Assert.True(heal.HookStored);
        Assert.True(heal.Armed);
    }

    [Fact]
    public void Detour_TheLoadersInnerLookup_NeverAsksForTheStackLimits()
    {
        int stackCalls = 0;
        var heap = new FakeHeap((HeapKey, KeyBytes(NameAddr)), (NameAddr, Padded(Name)));
        var heal = new IconCacheHeal(heap, () => { stackCalls++; return Stack; });
        nint inner = -1;
        heal.BindForTest((m, k) => 0, (m, u, f) => { int before = stackCalls; inner = heal.Detour(m, (nint)HeapKey); Assert.Equal(before, stackCalls); return 0; });
        heal.Track(new[] { Name });

        heal.Detour((nint)Manager, (nint)HeapKey);

        Assert.Equal(0, inner);
        Assert.Equal(1, stackCalls);
    }

    // ---- the canary ----

    [Fact]
    public void Armed_StaysFalseUntilTheDetourHasRunOnce()
    {
        var rig = new Rig();
        Assert.False(rig.Heal.Armed);
        rig.Look(StackKey);
        Assert.True(rig.Heal.Armed);
    }

    [Fact]
    public void Armed_IsFalseWhenNothingWasInstalled()
    {
        Assert.False(new IconCacheHeal(new FakeHeap(Array.Empty<(long, byte[])>()), () => Stack).Armed);
    }

    // ---- failure containment ----

    private sealed class ThrowingMem : IGameMemory
    {
        public byte U8(long addr) => 0;
        public ushort U16(long addr) => 0;
        public bool TryReadBytes(long addr, int len, out byte[] buf) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Detour_AThrowingRead_ReturnsTheMissAndWarnsOnce()
    {
        var heal = new IconCacheHeal(new ThrowingMem(), () => Stack);
        heal.BindForTest((m, k) => 0, (m, u, f) => 1);
        heal.Track(new[] { Name });
        using var cap = LogCapture.Start(LogLevel.Debug);

        Assert.Equal(0, heal.Detour((nint)Manager, (nint)HeapKey));
        Assert.Equal(0, heal.Detour((nint)Manager, (nint)HeapKey));

        Assert.Equal(1, cap.File.Count(l => l.Contains("IconCacheHeal")));
    }

    // ---- the builder guard ----

    [Fact]
    public void Detour_AFaultOnACachedHit_StillHandsBackTheEntry()
    {
        var heal = new IconCacheHeal(new FakeHeap(Array.Empty<(long, byte[])>()),
            () => throw new InvalidOperationException("stack limits gone"));
        heal.BindForTest((m, k) => (nint)Entry, (m, u, f) => 1);
        heal.Track(new[] { Name });

        Assert.Equal((nint)Entry, heal.Detour((nint)Manager, (nint)HeapKey));
    }

    private static Rig ClockedRig(out Func<long> now, out Action<long> set)
    {
        long t = 10_000;
        var rig = new Rig();
        rig.Heal.Clock = () => Volatile.Read(ref t);
        now = () => Volatile.Read(ref t);
        set = v => Volatile.Write(ref t, v);
        return rig;
    }

    [Fact]
    public void BuilderWindow_IsTwentyFiveHundredMs_AndOutlastsTheMeasuredInventoryBurst()
    {
        const int measuredBurstMs = 1770;
        Assert.Equal(2500, IconCacheHealPolicy.BuilderWindowMs);
        Assert.True(IconCacheHealPolicy.BuilderWindowMs > measuredBurstMs);
    }

    [Fact]
    public void Detour_AHeapMissRightAfterAStackKeyLookup_IsDeferredWithNoLoadAndNoAttempt()
    {
        var rig = ClockedRig(out _, out var set);
        rig.Look(StackKey);
        set(10_000 + 1770);

        Assert.Equal(0, rig.Look());

        Assert.Empty(rig.LoadCalls);
        Assert.Equal(0, rig.Heal.AttemptCount((nint)Manager, Name));
        Assert.Equal(0, rig.Heal.Healed + rig.Heal.Refused);
    }

    [Fact]
    public void Detour_ADeferredMiss_HealsOnceTheWindowHasPassed()
    {
        var rig = ClockedRig(out _, out var set);
        rig.Look(StackKey);
        set(10_000 + IconCacheHealPolicy.BuilderWindowMs - 1);
        Assert.Equal(0, rig.Look());
        set(10_000 + IconCacheHealPolicy.BuilderWindowMs);

        Assert.Equal((nint)Entry, rig.Look());
        Assert.Single(rig.LoadCalls);
    }

    [Fact]
    public void Detour_TheWindowIsPerManager()
    {
        var rig = ClockedRig(out _, out _);
        rig.Look(StackKey, Manager + 0x1000);

        Assert.Equal((nint)Entry, rig.Look());
    }

    [Fact]
    public void Detour_AHeapKeyHit_NeverStamps()
    {
        var rig = ClockedRig(out _, out _);
        rig.Loaded = true;
        Assert.Equal((nint)Entry, rig.Look());
        rig.Loaded = false;

        Assert.Equal((nint)Entry, rig.Look());   // healed at once, so the hit left no stamp
        Assert.Single(rig.LoadCalls);
    }

    [Fact]
    public void Detour_AStackKeyHit_StampsToo()
    {
        var rig = ClockedRig(out _, out _);
        rig.Loaded = true;
        rig.Look(StackKey);
        rig.Loaded = false;

        Assert.Equal(0, rig.Look());
        Assert.Empty(rig.LoadCalls);
    }

    [Fact]
    public void Detour_NothingTracked_StillStamps_SoAHideCanWaitForQuiet()
    {
        var rig = new Rig(track: false);
        long t = 10_000;
        rig.Heal.Clock = () => t;
        Assert.True(rig.Heal.BuildersQuiet);

        rig.Look(StackKey);
        Assert.False(rig.Heal.BuildersQuiet);

        t = 10_000 + IconCacheHealPolicy.BuilderWindowMs - 1;
        Assert.False(rig.Heal.BuildersQuiet);
        t = 10_000 + IconCacheHealPolicy.BuilderWindowMs;
        Assert.True(rig.Heal.BuildersQuiet);
    }

    [Fact]
    public void Detour_AHeapKeyLookup_NeverMakesBuildersBusy()
    {
        var rig = new Rig(track: false);
        rig.Heal.Clock = () => 10_000;
        rig.Look();
        rig.Loaded = true;
        rig.Look();
        Assert.True(rig.Heal.BuildersQuiet);
    }

    [Fact]
    public void Detour_ManyDeferredMisses_LogOneLinePerManagerAndNamePerWindow()
    {
        var rig = ClockedRig(out _, out var set);
        using var cap = LogCapture.Start(LogLevel.Debug);
        rig.Look(StackKey);
        for (int i = 0; i < 5; i++) rig.Look();
        Assert.Equal(1, cap.File.Count(l => l.Contains("heal deferred")));

        set(10_000 + 2000);
        rig.Look(StackKey);   // a fresh stamp starts a fresh window
        set(10_000 + 4400);
        rig.Look();
        Assert.Equal(2, cap.File.Count(l => l.Contains("heal deferred")));
    }

    [Fact]
    public void Detour_DeferralsNeverEatTheAttemptCap()
    {
        var rig = ClockedRig(out _, out var set);
        rig.LoadSucceeds = false;
        rig.Look(StackKey);
        for (int i = 0; i < 10; i++) rig.Look();
        set(10_000 + 2500);

        for (int i = 0; i < 3; i++) rig.Look();

        Assert.Equal(3, rig.LoadCalls.Count);
    }

    [Fact]
    public void Detour_TheLoadersOwnInnerLookup_PaysNothingExtra()
    {
        var rig = ClockedRig(out _, out _);
        rig.OnLoad = () => rig.Look(StackKey);   // the loader's inner lookup, on the healing thread

        Assert.Equal((nint)Entry, rig.Look());
        Assert.Equal(1, rig.Heal.Healed);
    }

    // ---- the page edge ----

    [Fact]
    public void Detour_ANameThatCrossesAPageEdge_StillReads()
    {
        long edge = 0x2_0010_0000L;
        int head = 20;
        var full = Padded(Name, 256);
        var first = full.Take(head).ToArray();
        var rest = full.Skip(head).ToArray();
        var heap = new FakeHeap((HeapKey, KeyBytes(edge - head)), (edge - head, first), (edge, rest));
        var rig = new Rig(heap: heap);

        Assert.Equal((nint)Entry, rig.Look());
        Assert.Equal(edge - head, rig.LoadCalls.Single().Uri);
    }

    // ---- many threads, one heal ----

    [Fact]
    public void Detour_ManyThreadsOnTheSameMiss_LoadExactlyOnce()
    {
        var rig = new Rig();
        rig.OnLoad = () => Thread.Sleep(50);
        var results = new nint[8];
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => results[i] = rig.Look());

        Assert.Single(rig.LoadCalls);
        Assert.All(results, r => Assert.Equal((nint)Entry, r));
    }

    // ---- arming ----

    private static readonly byte[] LookupBytes = IconCacheHeal.LookupPrologue;
    private static readonly byte[] LoadBytes = IconCacheHeal.LoadPrologue;

    [Fact]
    public void Prologues_VerifyAgainstThemselves_AndRefuseEachOther()
    {
        Assert.True(HookLandmark.Verify(LookupBytes, IconCacheHeal.LookupPrologue));
        Assert.True(HookLandmark.Verify(LoadBytes, IconCacheHeal.LoadPrologue));
        Assert.False(HookLandmark.Verify(LoadBytes, IconCacheHeal.LookupPrologue));
        Assert.False(HookLandmark.Verify(LookupBytes, IconCacheHeal.LoadPrologue));
    }

    [Fact]
    public void ProloguesMatch_ReadsBothTargetsThroughTheGuardedMemory()
    {
        long lookup = ImageBase.At(0x1403FBB80), load = ImageBase.At(0x1403FBE3C);
        var good = new FakeHeap((lookup, Pad(LookupBytes)), (load, Pad(LoadBytes)));
        Assert.True(new IconCacheHeal(good, () => Stack).ProloguesMatch());

        var swapped = new FakeHeap((lookup, Pad(LoadBytes)), (load, Pad(LookupBytes)));
        Assert.False(new IconCacheHeal(swapped, () => Stack).ProloguesMatch());

        var missing = new FakeHeap((lookup, Pad(LookupBytes)));
        Assert.False(new IconCacheHeal(missing, () => Stack).ProloguesMatch());
    }

    private static byte[] Pad(byte[] b) => b.Concat(new byte[32]).ToArray();

    [Fact]
    public void Arm_WithoutHooks_LogsOnePlainLineAndStaysUnarmed()
    {
        var heal = new IconCacheHeal(new FakeHeap(Array.Empty<(long, byte[])>()), () => Stack);
        using var cap = LogCapture.Start(LogLevel.Debug);

        heal.Arm(null);

        Assert.False(heal.Armed);
        Assert.Equal(1, cap.File.Count(l => l.Contains("same session glow rings are off")));
    }
}
