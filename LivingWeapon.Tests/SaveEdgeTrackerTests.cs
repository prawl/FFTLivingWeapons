using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-353: the save-edge core (key derivation and the two pending slots) and the
/// hooks' arm decision, all pure.</summary>
public class SaveEdgeTrackerTests
{
    internal static byte[] Header(uint playTime, byte tag = 0)
    {
        var h = new byte[Offsets.SaveHeaderKeyLen];
        h[0x12] = tag;   // +0x112 in the struct: one of the header bytes the serializer writes
        int pt = Offsets.SaveHeaderPlayTimeOff - Offsets.SaveHeaderKeyOff;
        h[pt] = (byte)playTime; h[pt + 1] = (byte)(playTime >> 8); h[pt + 2] = (byte)(playTime >> 16); h[pt + 3] = (byte)(playTime >> 24);
        return h;
    }

    [Fact]
    public void Key_is_stable_for_the_same_header_and_names_the_play_time()
    {
        string a = SaveEdgeTracker.KeyFromHeader(Header(2482));
        string b = SaveEdgeTracker.KeyFromHeader(Header(2482));
        Assert.Equal(a, b);
        Assert.StartsWith("pt2482-", a);
        Assert.Equal("pt2482-".Length + 12, a.Length);
    }

    [Fact]
    public void Key_changes_when_any_header_byte_changes()
    {
        Assert.NotEqual(SaveEdgeTracker.KeyFromHeader(Header(2482)), SaveEdgeTracker.KeyFromHeader(Header(2483)));
        Assert.NotEqual(SaveEdgeTracker.KeyFromHeader(Header(2482)), SaveEdgeTracker.KeyFromHeader(Header(2482, tag: 1)));
        var longer = new byte[Offsets.SaveHeaderKeyLen + 8];
        Array.Copy(Header(2482), longer, Offsets.SaveHeaderKeyLen);
        longer[^1] = 0xFF;
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(Header(2482)), SaveEdgeTracker.KeyFromHeader(longer));   // bytes past the window never count
        Assert.Throws<ArgumentException>(() => SaveEdgeTracker.KeyFromHeader(new byte[10]));
    }

    [Fact]
    public void Pending_slots_hand_out_each_edge_once_and_a_newer_edge_supersedes()
    {
        var t = new SaveEdgeTracker();
        Assert.Null(t.CurrentKey);
        Assert.False(t.TryTakePendingSave(out _, out _));
        Assert.False(t.TryTakePendingLoad(out _));

        t.OnSerialized(Header(100), new Dictionary<int, int> { [261] = 2 });
        t.OnSerialized(Header(101), new Dictionary<int, int> { [261] = 3 });   // supersedes
        Assert.True(t.TryTakePendingSave(out string key, out var counts));
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(Header(101)), key);
        Assert.Equal(3, counts[261]);
        Assert.False(t.TryTakePendingSave(out _, out _));
        Assert.Equal(key, t.CurrentKey);

        t.OnApplied(Header(50));
        Assert.True(t.TryTakePendingLoad(out string lk));
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(Header(50)), lk);
        Assert.False(t.TryTakePendingLoad(out _));
    }

    [Fact]
    public void Counts_handed_to_the_tracker_are_copied_not_shared()
    {
        var t = new SaveEdgeTracker();
        var live = new Dictionary<int, int> { [261] = 2 };
        t.OnSerialized(Header(7), live);
        live[261] = 9;
        Assert.True(t.TryTakePendingSave(out _, out var counts));
        Assert.Equal(2, counts[261]);
    }

    [Fact]
    public void Hooks_arm_only_on_their_exact_prologues()
    {
        Assert.True(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x20, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56, 0x41 }, SaveEdgeHooks.SerializePrologue));
        Assert.True(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x44, 0x0F, 0xB6, 0x1D, 0x3D }, SaveEdgeHooks.ApplyPrologue));
        Assert.True(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x8B, 0x15, 0xE2, 0x1C, 0xC6, 0x02, 0x85, 0xD2 }, SaveEdgeHooks.ApplyBPrologue));
        Assert.False(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x44, 0x0F, 0xB6, 0x1E }, SaveEdgeHooks.ApplyPrologue));
        Assert.False(SaveEdgeHooks.ShouldArm(false, SaveEdgeHooks.SerializePrologue, SaveEdgeHooks.SerializePrologue));
    }

    [Fact]
    public void ReadHeader_follows_the_struct_pointer_and_refuses_a_null_one()
    {
        var f = new FakeCodePatcher();
        var hooks = new SaveEdgeHooks(f, new SaveEdgeTracker(), new[] { 261 });
        Assert.Null(hooks.ReadHeader());                       // global unreadable
        f.Seed(Offsets.SaveStructPtr, 0, 0, 0, 0, 0, 0, 0, 0);
        Assert.Null(hooks.ReadHeader());                       // null pointer: not a save edge
        const long structAddr = 0x1F0000000L;
        f.Seed(Offsets.SaveStructPtr, BitConverter.GetBytes(structAddr));
        f.Seed(structAddr + Offsets.SaveHeaderKeyOff, Header(2482));
        var hdr = hooks.ReadHeader();
        Assert.NotNull(hdr);
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(Header(2482)), SaveEdgeTracker.KeyFromHeader(hdr!));
    }
}
