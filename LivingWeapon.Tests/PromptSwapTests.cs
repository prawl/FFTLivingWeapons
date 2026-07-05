using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PromptSwap.cs: the testable decision core behind facing-prompt toast delivery -- the
/// production toast-delivery path (BannerToast queue + PromptSwap decision core + PromptSwapHook
/// native detour), superseding the retired scheduler/spawner stack. No native calls here;
/// PromptSwapHook (the SetTextString detour) is untestable by construction and exercised only by
/// hand/eyewitness, mirroring ShowSpike's own v10/v11 tap (docs/research/CALLOUT_BANNER_JOURNEY.md).
/// </summary>
public class PromptSwapTests
{
    private const long TextPtr = 0x9000;
    private const string RealPrompt = "Select a facing direction and press <keyicon=ok>";

    /// <summary>Seeds a TerrainBlocks entry so FakeSparseMemory.TryReadBytes serves ASCII bytes for
    /// text (NUL-padded to bufLen -- mirrors a real fixed native buffer, and gives the ASCII decode
    /// a NUL to stop at for short strings like "Ramza").</summary>
    private static void SeedText(FakeSparseMemory mem, long ptr, string text, int bufLen = 64)
    {
        var buf = new byte[bufLen];
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, buf, Math.Min(bytes.Length, bufLen));
        mem.TerrainBlocks[ptr] = buf;
    }

    private static BannerToast NewToast() =>
        new(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), enabled: true);

    // ---- (1) LOAD-BEARING: real prompt text + a pending toast swaps and dequeues ----

    [Fact]
    public void Facing_prompt_with_pending_toast_swaps_and_dequeues()
    {
        var mem = new FakeSparseMemory();
        SeedText(mem, TextPtr, RealPrompt);
        var toast = NewToast();
        toast.Enqueue(1, 1, "Zwill Straightblade has grown to +!");
        var swap = new PromptSwap(toast, mem);

        bool ok = swap.TryPrepareSwap(TextPtr, out var payload);

        Assert.True(ok);
        Assert.Equal("Zwill Straightblade has grown to +!", payload);
        Assert.False(toast.HasPending);
    }

    // ---- (2) NON-VACUOUS NEGATIVE: prefix matches, empty queue -- vanilla fallback ----

    [Fact]
    public void Facing_prompt_with_empty_queue_passes_through()
    {
        var mem = new FakeSparseMemory();
        SeedText(mem, TextPtr, RealPrompt);
        var toast = NewToast();
        var swap = new PromptSwap(toast, mem);

        bool ok = swap.TryPrepareSwap(TextPtr, out _);

        Assert.False(ok);
    }

    // ---- (3) A non-facing commit never consumes a queued toast ----

    [Theory]
    [InlineData("Ramza")]
    [InlineData("Select a tile and press F to move")]
    public void Non_facing_text_never_consumes_a_toast(string text)
    {
        var mem = new FakeSparseMemory();
        SeedText(mem, TextPtr, text);
        var toast = NewToast();
        toast.Enqueue(1, 1, "payload");
        var swap = new PromptSwap(toast, mem);

        bool ok = swap.TryPrepareSwap(TextPtr, out _);

        Assert.False(ok);
        Assert.True(toast.HasPending);
    }

    // ---- (4) An unreadable text pointer passes through, toast still queued ----

    [Fact]
    public void Unreadable_text_ptr_passes_through()
    {
        var mem = new FakeSparseMemory();   // TextPtr never registered in TerrainBlocks
        var toast = NewToast();
        toast.Enqueue(1, 1, "payload");
        var swap = new PromptSwap(toast, mem);

        bool ok = swap.TryPrepareSwap(TextPtr, out _);

        Assert.False(ok);
        Assert.True(toast.HasPending);
    }

    // ---- (5) A null text pointer passes through ----

    [Fact]
    public void Null_text_ptr_passes_through()
    {
        var mem = new FakeSparseMemory();
        var toast = NewToast();
        toast.Enqueue(1, 1, "payload");
        var swap = new PromptSwap(toast, mem);

        bool ok = swap.TryPrepareSwap(0, out _);

        Assert.False(ok);
        Assert.True(toast.HasPending);
    }

    // ---- (6) Payload truncates to the 96-char cap ----

    [Fact]
    public void Payload_truncates_to_cap()
    {
        var mem = new FakeSparseMemory();
        SeedText(mem, TextPtr, RealPrompt);
        var toast = NewToast();
        string longPayload = new string('X', 150);
        toast.Enqueue(1, 1, longPayload);
        var swap = new PromptSwap(toast, mem);

        bool ok = swap.TryPrepareSwap(TextPtr, out var payload);

        Assert.True(ok);
        Assert.Equal(96, payload.Length);
        Assert.Equal(longPayload.Substring(0, 96), payload);
    }

    // ---- (7) Prefix match is an exact (Ordinal) prefix -- case matters ----

    [Fact]
    public void Prefix_match_is_exact_prefix()
    {
        var mem = new FakeSparseMemory();
        SeedText(mem, TextPtr, RealPrompt);
        var toast = NewToast();
        toast.Enqueue(1, 1, "payload");
        var swap = new PromptSwap(toast, mem);

        Assert.True(swap.TryPrepareSwap(TextPtr, out _));

        var mem2 = new FakeSparseMemory();
        SeedText(mem2, TextPtr, "select a facing direction and press <keyicon=ok>");
        var toast2 = NewToast();
        toast2.Enqueue(1, 1, "payload");
        var swap2 = new PromptSwap(toast2, mem2);

        bool ok = swap2.TryPrepareSwap(TextPtr, out _);

        Assert.False(ok);
        Assert.True(toast2.HasPending);
    }
}
