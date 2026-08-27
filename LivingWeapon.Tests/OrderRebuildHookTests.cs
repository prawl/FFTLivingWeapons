using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2: the order-rebuild hook's pure list policy and its whole detour body
/// (Process with a lambda standing in for the game's own rebuild). Ported from the rig's
/// OrderRebuildHookTests (FFTHandsFree b1abd77).</summary>
public class OrderRebuildHookTests
{
    private static readonly nint List = (nint)0x141811470;   // the live inventory buffer address, any value works

    private static byte[] Words(params ushort[] words)
    {
        var b = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++) { b[i * 2] = (byte)(words[i] & 0xFF); b[i * 2 + 1] = (byte)(words[i] >> 8); }
        return b;
    }

    private static FakeCodePatcher WithList(params ushort[] words)
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.Seed(List, Words(words));
        return f;
    }

    /// <summary>A stand-in for the game's rebuild: overwrite the list with <paramref name="after"/>
    /// (terminated) and return its count, exactly as the routine does.</summary>
    private static OrderRebuildHook.RebuildFn Rebuild(FakeCodePatcher f, params ushort[] after)
        => (table, list) =>
        {
            var w = new ushort[after.Length + 1];
            Array.Copy(after, w, after.Length);
            w[^1] = OrderRebuildHook.Terminator;
            f.TryWrite((long)list, Words(w));
            return after.Length;
        };

    [Fact]
    public void ParseList_stops_at_the_terminator_and_refuses_a_buffer_without_one()
    {
        Assert.Equal(new ushort[] { 1, 0x105, 3 }, OrderRebuildHook.ParseList(Words(1, 0x105, 3, 0xFFFF, 9)));
        Assert.Empty(OrderRebuildHook.ParseList(Words(0xFFFF, 5))!);
        Assert.Null(OrderRebuildHook.ParseList(Words(1, 2, 3)));
    }

    [Fact]
    public void DroppedWords_keeps_input_order_flags_and_reports_each_id_once()
    {
        var dropped = OrderRebuildHook.DroppedWords(new ushort[] { 1, 0x4105, 2, 0x106, 0x105 }, new ushort[] { 2, 1 });
        Assert.Equal(new ushort[] { 0x4105, 0x106 }, dropped);   // 0x105 appears once, first occurrence's flags kept
        Assert.Empty(OrderRebuildHook.DroppedWords(new ushort[] { 1, 2 }, new ushort[] { 0x4002, 1 }));   // masked compare
    }

    [Fact]
    public void TailBytes_is_the_dropped_words_then_the_terminator()
        => Assert.Equal(new byte[] { 0x05, 0x01, 0x06, 0x41, 0xFF, 0xFF }, OrderRebuildHook.TailBytes(new ushort[] { 0x105, 0x4106 }));

    [Fact]
    public void Process_reappends_what_the_rebuild_dropped_and_corrects_the_count()
    {
        var f = WithList(1, 2, 0x4105, 3, 0xFFFF);
        var hook = new OrderRebuildHook(f);
        int count = hook.Process((nint)0x1407B2550, List, Rebuild(f, 1, 2, 3));
        Assert.Equal(4, count);
        Assert.Equal(new ushort[] { 1, 2, 3, 0x4105 }, OrderRebuildHook.ParseList(f.Read(List, 12)));
        Assert.Equal(1, hook.Reappended);
    }

    [Fact]
    public void Process_passes_through_when_nothing_was_dropped_or_the_list_is_unreadable()
    {
        var f = WithList(1, 2, 0xFFFF);
        var hook = new OrderRebuildHook(f);
        Assert.Equal(2, hook.Process(0, List, Rebuild(f, 2, 1)));
        Assert.Equal(0, hook.Reappended);

        var blind = new FakeCodePatcher();   // nothing readable at all
        int calls = 0;
        Assert.Equal(7, new OrderRebuildHook(blind).Process(0, List, (_, __) => { calls++; return 7; }));
        Assert.Equal(1, calls);   // the original runs exactly once even on passthrough
    }

    [Fact]
    public void Process_refuses_to_grow_the_list_past_its_own_input_footprint()
    {
        var f = WithList(0x105, 0xFFFF);
        var hook = new OrderRebuildHook(f);
        // A pathological rebuild that returns MORE words than it was given: re-appending would
        // write past the input footprint, so the hook declines and returns the game's answer.
        Assert.Equal(2, hook.Process(0, List, Rebuild(f, 1, 2)));
        Assert.Equal(new ushort[] { 1, 2 }, OrderRebuildHook.ParseList(f.Read(List, 8)));
        Assert.Equal(0, hook.Reappended);
    }

    [Fact]
    public void Process_returns_the_games_count_when_the_reappend_write_is_refused()
    {
        var f = WithList(0x105, 1, 0xFFFF);
        f.RefuseWritesAt.Add(List + 2);   // the tail write lands right after the one kept word
        Assert.Equal(1, new OrderRebuildHook(f).Process(0, List, Rebuild(f, 1)));
    }

    [Fact]
    public void ShouldArm_requires_the_live_prologue()
    {
        Assert.True(OrderRebuildHook.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x18, 0x48, 0x89 }));
        Assert.False(OrderRebuildHook.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x19 }));
        Assert.False(OrderRebuildHook.ShouldArm(false, OrderRebuildHook.ExpectedPrologue));
        Assert.Equal(Offsets.FnOrderRebuild, new OrderRebuildHook(new FakeCodePatcher()).TargetAddr);
    }
}
