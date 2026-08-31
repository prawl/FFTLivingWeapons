using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-372: <see cref="ListBuilderHook"/>'s pure truncation policy and its whole detour
/// body (Process with a lambda standing in for the game's shared list builder). D2: a call handed
/// the game's own static buffer (<see cref="Offsets.MenuListBuffer"/>) is a pure passthrough;
/// every other buffer is routed through a mod-owned pool and truncated to
/// <see cref="ListBuilderHook.StackCallerCap"/> (149) on the way back. D3: the pool is four
/// try-acquire slots for REENTRANCY, released the instant the copy-back finishes.</summary>
public class ListBuilderHookTests
{
    private const long PoolBase = 0x150000000L;
    private static readonly nint StackBuffer = (nint)0x7FF000000L;   // any non-static address

    private static byte[] Words(params ushort[] words)
    {
        var b = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) { b[i * 2] = (byte)(words[i] & 0xFF); b[i * 2 + 1] = (byte)(words[i] >> 8); }
        return b;
    }

    private static ushort[] ReadWords(FakeCodePatcher f, long addr, int count)
    {
        var b = f.Read(addr, count * 2);
        var w = new ushort[count];
        for (int i = 0; i < count; i++) w[i] = (ushort)(b[i * 2] | (b[i * 2 + 1] << 8));
        return w;
    }

    /// <summary>Stand-in for the game's own builder: writes <paramref name="entries"/> ids
    /// followed by the terminator into whatever buffer (r9/list) it is handed, and returns the
    /// entry count -- the exact contract Process relies on (D2/Q5: it trusts the returned count,
    /// never re-scans for a terminator itself).</summary>
    private static ListBuilderHook.BuildFn Builder(FakeCodePatcher f, params ushort[] entries)
        => (a, b, c, list) =>
        {
            var w = new ushort[entries.Length + 1];
            Array.Copy(entries, w, entries.Length);
            w[^1] = ListBuilderHook.Terminator;
            f.TryWrite((long)list, Words(w));
            return entries.Length;
        };

    // --- Policy (pure) ---

    [Fact]
    public void TruncatedTail_keeps_at_most_cap_entries_then_the_terminator()
    {
        Assert.Equal(new byte[] { 1, 0, 2, 0, 0xFF, 0xFF }, ListBuilderHook.TruncatedTail(new ushort[] { 1, 2, 3 }, 2));
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0, 0xFF, 0xFF }, ListBuilderHook.TruncatedTail(new ushort[] { 1, 2, 3 }, 5));
        Assert.Equal(new byte[] { 0xFF, 0xFF }, ListBuilderHook.TruncatedTail(Array.Empty<ushort>(), 0));
    }

    [Fact]
    public void TruncatedCount_is_the_min_of_the_word_count_and_the_cap()
    {
        Assert.Equal(2, ListBuilderHook.TruncatedCount(new ushort[] { 1, 2, 3 }, 2));
        Assert.Equal(3, ListBuilderHook.TruncatedCount(new ushort[] { 1, 2, 3 }, 5));
        Assert.Equal(0, ListBuilderHook.TruncatedCount(Array.Empty<ushort>(), 0));
    }

    [Fact]
    public void SlotAddr_is_the_pool_base_plus_the_slot_index_times_slot_bytes()
    {
        Assert.Equal(PoolBase, ListBuilderHook.SlotAddr(PoolBase, 0));
        Assert.Equal(PoolBase + ListBuilderHook.SlotBytes, ListBuilderHook.SlotAddr(PoolBase, 1));
        Assert.Equal(PoolBase + 3L * ListBuilderHook.SlotBytes, ListBuilderHook.SlotAddr(PoolBase, 3));
    }

    // --- U1 (LOAD-BEARING, the non-vacuous negative) ---

    [Fact]
    public void A_stack_buffer_call_runs_against_the_pool_and_copies_149_back()
    {
        // The relocated-cookie proof (replaces the retired TemplateRelocationTests T9b): the
        // SAME constant the truncation uses actually fits the 152-word stack buffers.
        Assert.True((ListBuilderHook.StackCallerCap + 3) * 2 <= Offsets.ListBuilderStackRoomBytes);

        var entries = new ushort[200];
        for (int i = 0; i < entries.Length; i++) entries[i] = (ushort)(i + 1);
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(f, poolBase: PoolBase);

        int result = hook.Process(0, 0, 0, StackBuffer, Builder(f, entries));

        Assert.Equal(ListBuilderHook.StackCallerCap, result);
        var kept = ReadWords(f, (long)StackBuffer, ListBuilderHook.StackCallerCap + 1);
        for (int i = 0; i < ListBuilderHook.StackCallerCap; i++) Assert.Equal(entries[i], kept[i]);
        Assert.Equal(ListBuilderHook.Terminator, kept[ListBuilderHook.StackCallerCap]);
        // The pool slot, not the caller's buffer, received the original's full 200-entry write.
        Assert.Equal(entries, ReadWords(f, PoolBase, 200));
        // Named break: removing the min from TruncatedTail (copying all 200 back) would put the
        // terminator at index 200, not StackCallerCap -- the assertion above would fail.
    }

    // --- U2 ---

    [Fact]
    public void A_static_buffer_call_is_a_pure_passthrough()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(f, poolBase: PoolBase);
        var staticList = (nint)Offsets.MenuListBuffer;
        int calls = 0;
        nint seenList = 0;

        int result = hook.Process(1, 2, 3, staticList, (a, b, c, list) => { calls++; seenList = list; return 42; });

        Assert.Equal(1, calls);
        Assert.Equal(staticList, seenList);   // the original sees the static buffer itself
        Assert.Equal(42, result);
        Assert.Empty(f.Writes);   // no pool, no copy-back write into anything
        Assert.Equal(1, hook.Passthroughs);
    }

    // --- U3 ---

    [Fact]
    public void A_short_list_copies_back_unchanged()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(f, poolBase: PoolBase);

        int result = hook.Process(0, 0, 0, StackBuffer, Builder(f, 5, 9, 0x105));

        Assert.Equal(3, result);
        Assert.Equal(new ushort[] { 5, 9, 0x105 }, ReadWords(f, (long)StackBuffer, 3));
        Assert.Equal(ListBuilderHook.Terminator, ReadWords(f, (long)StackBuffer + 6, 1)[0]);
    }

    // --- U4 ---

    [Fact]
    public void Pool_exhaustion_writes_an_empty_list_and_returns_0()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(f, poolBase: PoolBase);
        for (int i = 0; i < ListBuilderHook.PoolSlots; i++) Assert.True(hook.AcquireSlot() >= 0);   // hold every slot

        int calls = 0;
        int result = hook.Process(0, 0, 0, StackBuffer, (a, b, c, list) => { calls++; return 7; });

        Assert.Equal(0, result);
        Assert.Equal(0, calls);   // D3: no safe buffer exists to hand the original at all
        Assert.Equal(ListBuilderHook.Terminator, ReadWords(f, (long)StackBuffer, 1)[0]);
        Assert.Equal(1, hook.PoolExhaustedCount);
    }

    // --- U4b ---

    [Fact]
    public void A_completed_call_releases_its_slot()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(f, poolBase: PoolBase);

        // Five sequential calls with only four slots: without the finally-release, the fifth
        // would find every slot still held and refuse (a menu listing nothing).
        for (int i = 0; i < ListBuilderHook.PoolSlots + 1; i++)
            Assert.Equal(2, hook.Process(0, 0, 0, StackBuffer, Builder(f, 1, 2)));

        Assert.Equal(0, hook.PoolExhaustedCount);
    }

    // --- U5 (REWRITTEN v1.2, F1 BLOCKER) ---

    /// <summary>Throws on every read but delegates writes to a real <see cref="FakeCodePatcher"/>,
    /// so U5 can observe the best-effort empty-terminator write even though the very read that
    /// triggers the refusal never completes.</summary>
    private sealed class ReadThrowingCodePatcher : ICodePatcher
    {
        private readonly FakeCodePatcher _inner;
        public ReadThrowingCodePatcher(FakeCodePatcher inner) => _inner = inner;
        public bool TryRead(long address, int count, out byte[] bytes) => throw new InvalidOperationException("boom");
        public bool TryWrite(long address, byte[] bytes) => _inner.TryWrite(address, bytes);
    }

    [Fact]
    public void A_failed_copy_back_refuses_with_an_empty_list_never_the_untruncated_count()
    {
        var inner = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(new ReadThrowingCodePatcher(inner), poolBase: PoolBase);
        int calls = 0;

        int result = hook.Process(0, 0, 0, StackBuffer, (a, b, c, list) => { calls++; return 5; });

        Assert.Equal(1, calls);   // the original still ran exactly once, outside the guard
        // v1.2 (F1): the v1.1 wording pinned `result == count` here -- the exact defect this
        // rewrite exists to catch. The original's world lives in the pool slot (this fake never
        // touches it), not the caller's buffer, so its count must never come back when the
        // copy-back itself never completed.
        Assert.Equal(0, result);
        Assert.Equal(ListBuilderHook.Terminator, ReadWords(inner, (long)StackBuffer, 1)[0]);
        Assert.Equal(1, hook.CopyBackRefusals);
    }

    // --- U5b (NEW v1.2, F1) ---

    [Fact]
    public void A_refused_caller_buffer_write_returns_0()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.RefuseWritesAt.Add((long)StackBuffer);
        var hook = new ListBuilderHook(f, poolBase: PoolBase);

        int result = hook.Process(0, 0, 0, StackBuffer, Builder(f, 1, 2, 3));

        Assert.Equal(0, result);
        Assert.Equal(1, hook.CopyBackRefusals);
        // The refusal is total, never partial: FakeCodePatcher's RefuseWritesAt blocks the whole
        // write before a single byte lands, so nothing was written past the caller's buffer start.
        Assert.False(f.Bytes.ContainsKey((long)StackBuffer));
    }

    // --- Q12 (v1.3): the zero-length-read semantic ---

    /// <summary>Wraps a <see cref="FakeCodePatcher"/> but mimics LiveCodePatcher's real refusal of
    /// a <c>count &lt;= 0</c> read (Memory/CodePatcher.cs ~:49); the plain fake accepts a
    /// zero-length read trivially, which is exactly the divergence Q12 names -- a test against the
    /// plain fake could not discriminate the fixed short-circuit from the unfixed code (both would
    /// already return 0 there, since a plain fake's zero-length TryRead just trivially succeeds).
    /// Chosen over "assert CopyBackRefusals == 0 against a plain fake" precisely because that
    /// version would be green even on the pre-Q12 code -- vacuous, not discriminating.</summary>
    private sealed class ZeroRefusingCodePatcher : ICodePatcher
    {
        private readonly FakeCodePatcher _inner;
        public ZeroRefusingCodePatcher(FakeCodePatcher inner) => _inner = inner;
        public bool TryRead(long address, int count, out byte[] bytes)
        {
            if (count <= 0) { bytes = Array.Empty<byte>(); return false; }
            return _inner.TryRead(address, count, out bytes);
        }
        public bool TryWrite(long address, byte[] bytes) => _inner.TryWrite(address, bytes);
    }

    [Fact]
    public void An_empty_build_returns_0_without_a_refusal()
    {
        var inner = new FakeCodePatcher { ZeroFillUnseeded = true };
        var hook = new ListBuilderHook(new ZeroRefusingCodePatcher(inner), poolBase: PoolBase);

        int result = hook.Process(0, 0, 0, StackBuffer, (a, b, c, list) => 0);   // a legitimately empty build

        Assert.Equal(0, result);
        Assert.Equal(ListBuilderHook.Terminator, ReadWords(inner, (long)StackBuffer, 1)[0]);
        // Q12: an empty list is a RESULT, not a refusal -- the pre-fix code took the (live-refused)
        // zero-length slot read and ticked this counter for a build that never actually failed.
        Assert.Equal(0, hook.CopyBackRefusals);
    }

    // --- prologue ---

    [Fact]
    public void ShouldArm_requires_the_live_prologue()
    {
        Assert.True(ListBuilderHook.ShouldArm(true, ListBuilderHook.ExpectedPrologue));
        var wrong = (byte[])ListBuilderHook.ExpectedPrologue.Clone();
        wrong[0] = 0x90;
        Assert.False(ListBuilderHook.ShouldArm(true, wrong));
        Assert.False(ListBuilderHook.ShouldArm(false, ListBuilderHook.ExpectedPrologue));
        Assert.Equal(Offsets.FnListBuilder, new ListBuilderHook(new FakeCodePatcher()).TargetAddr);
    }
}
