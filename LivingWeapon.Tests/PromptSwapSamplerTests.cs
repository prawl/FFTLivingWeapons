using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-89: PromptSwap's unique-prompt-head sampler. The 1.5.1 facing-prompt text silently stopped
/// matching FacingPromptPrefix and TryPrepareSwap's mismatch path is silent by design (see
/// PromptSwap's class doc), so the sampler logs the first dozen unique decodable prompt heads a
/// session ever sees at Debug tier, turning any session's log file into the live diagnostic.
/// Installs a fake FileConsoleLogger via ModLogger.Instance (mirrors ModLoggerFacadeTests) and
/// restores NullLogger in a finally so no other test observes this swap.
/// </summary>
public class PromptSwapSamplerTests
{
    private const long TextPtr = 0x9000;
    private const string RealPrompt = "Select a facing direction and press <keyicon=ok>";

    /// <summary>Mirrors PromptSwapTests.SeedText: NUL-padded fixed buffer, one head per call so
    /// re-seeding the same pointer simulates the game committing successive prompt texts through
    /// the same hooked address.</summary>
    private static void SeedText(FakeSparseMemory mem, long ptr, string text, int bufLen = 64)
    {
        var buf = new byte[bufLen];
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        Array.Copy(bytes, buf, Math.Min(bytes.Length, bufLen));
        mem.TerrainBlocks[ptr] = buf;
    }

    private static BannerToast NewToast() =>
        new(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), enabled: true);

    private static List<string> InstallFileCapture()
    {
        var console = new List<string>();
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(console.Add, file.Add) { LogLevel = LogLevel.Debug };
        return file;
    }

    // (1) LOAD-BEARING / RED-FIRST: two different non-matching decodable heads both sample.

    [Fact]
    public void Two_different_non_matching_heads_both_get_sampled()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            SeedText(mem, TextPtr, "Ramza");
            swap.TryPrepareSwap(TextPtr, out _);

            SeedText(mem, TextPtr, "Select a tile and press F to move");
            swap.TryPrepareSwap(TextPtr, out _);

            Assert.Contains(file, l => l.Contains("prompt head sample") && l.Contains("Ramza"));
            Assert.Contains(file, l => l.Contains("prompt head sample") && l.Contains("Select a tile"));
            Assert.Equal(2, file.Count(l => l.Contains("prompt head sample")));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (2) Dedupe: the same head text sampled twice logs once.

    [Fact]
    public void Same_head_sampled_twice_logs_once()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            SeedText(mem, TextPtr, "Ramza");
            swap.TryPrepareSwap(TextPtr, out _);
            swap.TryPrepareSwap(TextPtr, out _);

            Assert.Single(file, l => l.Contains("prompt head sample"));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (3) Bound: SampleCap+2 distinct texts log exactly SampleCap lines.

    [Fact]
    public void Bound_caps_at_SampleCap_lines_for_more_distinct_heads()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            const int SampleCap = 12;
            for (int i = 0; i < SampleCap + 2; i++)
            {
                SeedText(mem, TextPtr, $"Head{i:00}");
                swap.TryPrepareSwap(TextPtr, out _);
            }

            Assert.Equal(SampleCap, file.Count(l => l.Contains("prompt head sample")));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (4) Pure observer: sampling never dequeues.

    [Fact]
    public void Sampling_a_non_matching_head_never_dequeues_a_pending_toast()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var toast = NewToast();
            toast.Enqueue(1, 1, "payload");
            var swap = new PromptSwap(toast, mem);

            SeedText(mem, TextPtr, "Select a tile and press F to move");
            bool ok = swap.TryPrepareSwap(TextPtr, out _);

            Assert.False(ok);
            Assert.True(toast.HasPending);
            Assert.Contains(file, l => l.Contains("prompt head sample") && l.Contains("Select a tile"));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (5) Matched path unchanged: delivery still happens, sampler is a pure observer.

    [Fact]
    public void Matched_facing_prompt_still_delivers_the_payload()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var toast = NewToast();
            toast.Enqueue(1, 1, "Zwill Straightblade has grown to +!");
            var swap = new PromptSwap(toast, mem);

            SeedText(mem, TextPtr, RealPrompt);
            bool ok = swap.TryPrepareSwap(TextPtr, out var payload);

            Assert.True(ok);
            Assert.Equal("Zwill Straightblade has grown to +!", payload);
            Assert.False(toast.HasPending);
            // Delivery is unaffected regardless of whether the sampler also fired for this head.
            Assert.True(file.Count(l => l.Contains("prompt head sample")) <= 1);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (6) Undecodable heads (a non-ASCII lead byte) are never sampled.

    [Fact]
    public void Undecodable_head_is_not_sampled()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            // A control byte (0x01) before any NUL is a hard TryDecodeAscii mismatch (see
            // PromptSwap.TryDecodeAscii): the sampler must never see this head at all.
            var raw = new byte[16];
            raw[0] = 0x01;
            mem.TerrainBlocks[TextPtr] = raw;

            bool ok = swap.TryPrepareSwap(TextPtr, out _);

            Assert.False(ok);
            Assert.Empty(file.Where(l => l.Contains("prompt head sample")));
        }
        finally { ModLogger.UseNullLogger(); }
    }
}
