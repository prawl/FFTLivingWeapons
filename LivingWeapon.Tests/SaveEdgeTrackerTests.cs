using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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

    /// <summary>The two 0xB8 headers the round-trip probe read live on 2026-08-28
    /// (tools/probes/lw353_header_roundtrip.py, 01:09:27 and 01:09:53), copied verbatim from its
    /// log: two different saves at rest, differing only in the play-time u32 at +0x1B4 (2219 and
    /// 2584 seconds). It printed their keys as pt2219-4ee338125549 and pt2584-ac68fc314b36.</summary>
    private const string RestingHeaderPt2219 =
        "ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 fe 4c 63 05 04 09 04 " +
        "01 01 00 02 00 00 00 ff 44 07 00 00 00 00 00 00 03 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 02 00 00 00 ff ff ff ff ff ff ff ff " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "ff ff ff ff ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 ff ff ff ff ff ff ff ff 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ff ff ff ff ff ff ff ff " +
        "ff ff ff ff ff ff ff ff ff ff ff ff ab 08 00 00";

    private const string RestingHeaderPt2584 =
        "ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 fe 4c 63 05 04 09 04 " +
        "01 01 00 02 00 00 00 ff 44 07 00 00 00 00 00 00 03 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 02 00 00 00 ff ff ff ff ff ff ff ff " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "ff ff ff ff ff ff ff ff 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 ff ff ff ff ff ff ff ff 00 00 00 00 00 00 00 00 " +
        "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 ff ff ff ff ff ff ff ff " +
        "ff ff ff ff ff ff ff ff ff ff ff ff 18 0a 00 00";

    internal static byte[] Hex(string spaced)
    {
        string[] parts = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) b[i] = Convert.ToByte(parts[i], 16);
        return b;
    }

    /// <summary>THE round-4 test. The owner's live test 2 (2026-08-28 00:59-01:10) proved the
    /// hooks fire, but no save ever recognized its own load: three header bytes are save-in-flight
    /// markers. At the instant DetourSerialize samples (right after the serializer returns) the
    /// window offsets 0x1A, 0x1C and 0x1D (header +0x11A, +0x11C, +0x11D) read 0xFF; at rest, the
    /// state every observed load sampled, they read 0x00. Everything else in +0x100..+0x1B8
    /// round-trips byte-identically. Offline proof: setting exactly those three bytes to 0xFF in a
    /// resting header of the shape below reproduces all five save keys the mod logged that night
    /// (pt2200-f0fefc093c1b, pt2212-7423df91f46f, pt2219-0dbabc353233, pt2269-fb60d7fd7608,
    /// pt2584-f25ab97abe23), and zeroing them before hashing makes save key equal load key for
    /// every observed pair. Only the save edge was seen sampling in flight; all four logged loads
    /// sampled at rest, where the mask changes nothing, so it rides both edges defensively.</summary>
    [Fact]
    public void Key_masks_the_save_transient_bytes()
    {
        byte[] resting = Hex(RestingHeaderPt2219);
        Assert.Equal(Offsets.SaveHeaderKeyLen, resting.Length);
        byte[] inFlight = (byte[])resting.Clone();
        inFlight[0x1A] = 0xFF; inFlight[0x1C] = 0xFF; inFlight[0x1D] = 0xFF;
        Assert.NotEqual(resting, inFlight);   // the two vectors really do differ: no vacuous pass

        // The same save, sampled at either edge, keys identically, and the key is the one the
        // watch printed for the resting state (its three marker bytes are already 0x00 there, so
        // masking cannot move it). The in-flight state used to key pt2219-0dbabc353233, which is
        // what the mod wrote to the sidecar and never found again.
        Assert.Equal("pt2219-4ee338125549", SaveEdgeTracker.KeyFromHeader(resting));
        Assert.Equal(SaveEdgeTracker.KeyFromHeader(resting), SaveEdgeTracker.KeyFromHeader(inFlight));
        Assert.NotEqual("pt2219-0dbabc353233", SaveEdgeTracker.KeyFromHeader(inFlight));

        // Lockstep: the offsets themselves live in Offsets.cs, so a re-anchor has to move the
        // constant and this test together.
        Assert.Equal(new[] { 0x1A, 0x1C, 0x1D }, Offsets.SaveHeaderVolatileOffs);

        // Self-consistent: the key is sha1 over the window with exactly those three offsets zeroed.
        byte[] masked = (byte[])resting.Clone();
        masked[0x1A] = 0; masked[0x1C] = 0; masked[0x1D] = 0;
        string expected = "pt2219-" + Convert.ToHexString(SHA1.HashData(masked), 0, 6).ToLowerInvariant();
        Assert.Equal(expected, SaveEdgeTracker.KeyFromHeader(inFlight));

        // Two different saves still key differently (the mask drops markers, not identity).
        byte[] other = Hex(RestingHeaderPt2584);
        Assert.Equal("pt2584-ac68fc314b36", SaveEdgeTracker.KeyFromHeader(other));
        Assert.NotEqual(SaveEdgeTracker.KeyFromHeader(resting), SaveEdgeTracker.KeyFromHeader(other));
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

    /// <summary>The three "true" literals are the bytes the 1.5.2 exe really carries at each entry
    /// (serializer 0x140218F78, load-apply 0x14021B0E8, second restore 0x14021DE98), read longer than
    /// the expected prologue so the prefix compare is exercised on real code, not on a copy of the
    /// constant.</summary>
    [Fact]
    public void Hooks_arm_only_on_their_exact_prologues()
    {
        Assert.True(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x20, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56, 0x41 }, SaveEdgeHooks.SerializePrologue));
        Assert.True(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48, 0x89, 0x78, 0x20, 0x41, 0x56 }, SaveEdgeHooks.ApplyPrologue));
        Assert.True(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57, 0x41, 0x54, 0x41, 0x55, 0x41, 0x56, 0x41, 0x57 }, SaveEdgeHooks.ApplyBPrologue));
        Assert.False(SaveEdgeHooks.ShouldArm(true, new byte[] { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x49 }, SaveEdgeHooks.ApplyPrologue));
        Assert.False(SaveEdgeHooks.ShouldArm(false, SaveEdgeHooks.SerializePrologue, SaveEdgeHooks.SerializePrologue));
    }

    /// <summary>The landmark lockstep. Provenance, all read on 2026-08-27 late night from the
    /// running 1.5.2 process and the exe on disk: the struct-pointer global is 0x140D407A0, taken
    /// from the load-apply's own read of it, <c>4C 8B 05 98 56 B2 00</c> at 0x14021B101 (next ip
    /// 0x14021B108 + 0xB25698); the serializer reads the same global at 0x140218F8E, and an image
    /// xref sweep (tools/probes/lw346_xref_scan.py) counts 46 references to 0x140D407A0 and none to
    /// 0x141D407A0. 0x14021B070 is the SAVE wrapper, not the apply: it clears the header bytes and
    /// tail-jumps to the serializer at 0x14021B0E3, so the real load-apply begins at the next byte,
    /// 0x14021B0E8, and owns the bag copy into 0x1411A7C00 at 0x14021B1D5. The second restore
    /// routine (its bag copy at 0x14021E1D1 zeroes count[0] first) begins at 0x14021DE98; 0x14021DDF0
    /// is the async file-op stepper, which runs every frame for saves as well as loads.
    /// WHY THE LOCKSTEP EXISTS: on 2026-08-27 a one-digit transcription error (0x141D407A0 for
    /// 0x140D407A0) plus two entries taken from the wrong routines left every hook reporting
    /// installed while the feature did nothing, and no other test in this file could see it, because
    /// they all use the constants symbolically. A re-anchor must change this test and Offsets.cs in
    /// the same commit.</summary>
    [Fact]
    public void Save_edge_landmarks_are_the_ones_read_from_the_exe()
    {
        Assert.Equal(0x140D407A0L, Offsets.SaveStructPtr);
        Assert.Equal(0x140218F78L, Offsets.FnSaveSerialize);
        Assert.Equal(0x14021B0E8L, Offsets.FnSaveApply);
        Assert.Equal(0x14021DE98L, Offsets.FnSaveApplyB);
        Assert.Equal(0x142C81C80L, Offsets.SaveStructStatic);
        Assert.NotEqual(0x14021B070L, Offsets.FnSaveApply);    // the save wrapper, one jmp short of the apply
        Assert.NotEqual(0x14021DDF0L, Offsets.FnSaveApplyB);   // the async file-op stepper, not a restore
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
