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

    // LW-89 step 1b: bounded STRUCT sampler (the rdx-is-an-object hypothesis).
    //
    // Builds a 32-byte raw block with u64s planted at offsets 0x00 (q0), 0x10 (q10) and 0x18
    // (q18), mirroring the layout SampleStruct reads back out of textPtr.

    private const int StructSampleCap = 6;

    private static byte[] BuildRaw(int len, long q0 = 0, long q10 = 0, long q18 = 0)
    {
        var raw = new byte[len];
        Array.Copy(BitConverter.GetBytes(q0), 0, raw, 0x00, 8);
        Array.Copy(BitConverter.GetBytes(q10), 0, raw, 0x10, 8);
        Array.Copy(BitConverter.GetBytes(q18), 0, raw, 0x18, 8);
        return raw;
    }

    // (7) LOAD-BEARING / RED-FIRST: one commit through TryPrepareSwap logs exactly one struct
    // sample line carrying both the pointer and the q18 field the capacity-check hypothesis cares
    // about.

    [Fact]
    public void Struct_sample_logs_one_line_with_rdx_and_q18()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            mem.TerrainBlocks[TextPtr] = BuildRaw(32);
            var swap = new PromptSwap(NewToast(), mem);

            swap.TryPrepareSwap(TextPtr, out _);

            var lines = file.Where(l => l.Contains("prompt struct sample")).ToList();
            Assert.Single(lines);
            Assert.Contains("rdx=0x", lines[0]);
            Assert.Contains("q18=", lines[0]);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (8) Dedupe: the same 16 bytes at textPtr sampled twice logs once.

    [Fact]
    public void Same_struct_bytes_sampled_twice_logs_once()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            mem.TerrainBlocks[TextPtr] = BuildRaw(32);
            var swap = new PromptSwap(NewToast(), mem);

            swap.TryPrepareSwap(TextPtr, out _);
            swap.TryPrepareSwap(TextPtr, out _);

            Assert.Single(file, l => l.Contains("prompt struct sample"));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (9) Bound: StructSampleCap+2 distinct byte patterns log exactly StructSampleCap lines.

    [Fact]
    public void Struct_bound_caps_at_StructSampleCap_lines_for_more_distinct_samples()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            for (int i = 0; i < StructSampleCap + 2; i++)
            {
                mem.TerrainBlocks[TextPtr] = BuildRaw(32, q0: i + 1);
                swap.TryPrepareSwap(TextPtr, out _);
            }

            Assert.Equal(StructSampleCap, file.Count(l => l.Contains("prompt struct sample")));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (10) An unreadable textPtr still logs an unreadable-form line (with the pointer value) and
    // counts toward the bound, so a session that never gets a readable rdx cannot starve the cap
    // AND cannot burn it forever with a single repeated failure either.

    [Fact]
    public void Unreadable_struct_ptr_logs_unreadable_form_and_consumes_a_slot()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory(); // TextPtr never registered: a genuinely unreadable rdx.
            var swap = new PromptSwap(NewToast(), mem);

            swap.TryPrepareSwap(TextPtr, out _);

            var lines = file.Where(l => l.Contains("prompt struct sample")).ToList();
            Assert.Single(lines);
            Assert.Contains("unreadable", lines[0]);
            Assert.Contains($"rdx=0x{TextPtr:X}", lines[0]);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    [Fact]
    public void Unreadable_sample_counts_toward_the_bound()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            // Slot 1: genuinely unreadable (nothing registered yet).
            swap.TryPrepareSwap(TextPtr, out _);

            // Slots 2..StructSampleCap: distinct readable samples.
            for (int i = 0; i < StructSampleCap - 1; i++)
            {
                mem.TerrainBlocks[TextPtr] = BuildRaw(32, q0: i + 1);
                swap.TryPrepareSwap(TextPtr, out _);
            }

            Assert.Equal(StructSampleCap, file.Count(l => l.Contains("prompt struct sample")));

            // A further distinct sample must NOT log: the cap (unreadable slot included) is spent.
            mem.TerrainBlocks[TextPtr] = BuildRaw(32, q0: 999);
            swap.TryPrepareSwap(TextPtr, out _);

            Assert.Equal(StructSampleCap, file.Count(l => l.Contains("prompt struct sample")));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (11) The deref path renders both hex and a printable-ASCII view (dots for non-printables)
    // for a seeded pointer chain: q0 points at another registered block holding known text.

    [Fact]
    public void Deref_renders_hex_and_printable_ascii_for_a_seeded_pointer_chain()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            const long Q0 = 0xA000;
            mem.TerrainBlocks[TextPtr] = BuildRaw(32, q0: Q0);

            var derefBytes = new byte[16];
            var known = System.Text.Encoding.ASCII.GetBytes("Facing prompt!");
            Array.Copy(known, derefBytes, known.Length);
            derefBytes[14] = 0x01; // non-printable: must render as a dot
            derefBytes[15] = (byte)'X';
            mem.TerrainBlocks[Q0] = derefBytes;

            var swap = new PromptSwap(NewToast(), mem);
            swap.TryPrepareSwap(TextPtr, out _);

            var line = Assert.Single(file, l => l.Contains("prompt struct sample"));
            Assert.Contains("deref=", line);
            Assert.Contains(Convert.ToHexString(derefBytes), line);
            Assert.Contains("Facing prompt!.X", line);
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (12) Pure observer: struct sampling never changes TryPrepareSwap's return value or the
    // toast queue, even on the matched delivery path.

    [Fact]
    public void Struct_sampling_is_a_pure_observer_and_does_not_affect_delivery()
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
            Assert.Single(file, l => l.Contains("prompt struct sample"));
        }
        finally { ModLogger.UseNullLogger(); }
    }

    // (13) Both samplers fire independently on the same commit.

    [Fact]
    public void Head_sampler_and_struct_sampler_both_fire_on_the_same_commit()
    {
        var file = InstallFileCapture();
        try
        {
            var mem = new FakeSparseMemory();
            var swap = new PromptSwap(NewToast(), mem);

            SeedText(mem, TextPtr, "Ramza");
            swap.TryPrepareSwap(TextPtr, out _);

            Assert.Single(file, l => l.Contains("prompt head sample"));
            Assert.Single(file, l => l.Contains("prompt struct sample"));
        }
        finally { ModLogger.UseNullLogger(); }
    }
}
