using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A1: hot pass must respect the shared byte budget and rotate un-scanned hot chunks
/// so no chunk is permanently starved.
/// A2: hot chunks with no recent MarkHot calls must be evicted after HotTtlMs.
/// </summary>
public class DisplaySweepHotBudgetTests
{
    // ─── A1: hot phase respects budget ────────────────────────────────────────

    /// <summary>
    /// A Tick with a budget just big enough for one hot chunk must not offer more
    /// than ~budget bytes from hot chunks; subsequent Ticks must cover the remainder,
    /// and no chunk is permanently starved.
    /// </summary>
    [Fact]
    public void Hot_budget_limits_chunks_offered_per_tick()
    {
        // Four hot regions, each exactly one chunk
        long[] bases = { 0x10_0000_0000L, 0x11_0000_0000L, 0x12_0000_0000L, 0x13_0000_0000L };
        var regions = new List<(long, byte[])>();
        foreach (long b in bases)
            regions.Add((b, new byte[DisplaySweep.ChunkSize]));

        var heap = new FakeHeap(regions.ToArray());
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        // Drain the background generation first, then mark all four chunks hot
        var cap = new DisplaySweepTestBase.Capture();
        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, cap.Handler);
        foreach (long b in bases) sw.MarkHot(b);
        cap.Chunks.Clear();

        // Budget = exactly one chunk worth
        long oneChunk = DisplaySweep.ChunkSize;
        now += DisplaySweep.HotRescanMs + 1;
        sw.Tick(oneChunk, cap.Handler);

        // At most 2 chunks should have been offered (one minimum + possible lookback)
        Assert.True(cap.Chunks.Count <= 2,
            $"hot pass ignored budget: {cap.Chunks.Count} chunks offered with budget for 1");
    }

    /// <summary>
    /// Across enough Ticks (each with a one-chunk budget) every hot chunk is eventually
    /// offered -- no chunk is permanently starved by the rotation.
    /// </summary>
    [Fact]
    public void Hot_budget_rotation_eventually_covers_all_hot_chunks()
    {
        long[] bases = { 0x20_0000_0000L, 0x21_0000_0000L, 0x22_0000_0000L, 0x23_0000_0000L };
        var regions = new List<(long, byte[])>();
        foreach (long b in bases)
            regions.Add((b, new byte[DisplaySweep.ChunkSize]));

        var heap = new FakeHeap(regions.ToArray());
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        var cap = new DisplaySweepTestBase.Capture();
        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, cap.Handler);
        foreach (long b in bases) sw.MarkHot(b);
        cap.Chunks.Clear();

        // Re-mark on every tick so chunks stay alive; use budget = 1 chunk
        long oneChunk = DisplaySweep.ChunkSize;
        var seen = new HashSet<long>();
        for (int tick = 0; tick < 20; tick++)
        {
            now += DisplaySweep.HotRescanMs + 1;
            var pass = new DisplaySweepTestBase.Capture();
            sw.Tick(oneChunk, pass.Handler);
            foreach (var (cs, _, _) in pass.Chunks)
                seen.Add(cs);
            // Re-mark so TTL doesn't expire chunks during this test
            foreach (long b in bases) sw.MarkHot(b);
        }

        foreach (long b in bases)
            Assert.Contains(b, seen);
    }

    // ─── A2: hot chunks decay after TTL ───────────────────────────────────────

    /// <summary>
    /// A hot chunk that is NOT re-marked must stop being offered once HotTtlMs elapses.
    /// </summary>
    [Fact]
    public void Hot_chunk_not_remarked_is_dropped_after_ttl()
    {
        long regionBase = 0x30_0000_0000L;
        var heap = DisplaySweepTestBase.OneRegion(regionBase, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        var cap = new DisplaySweepTestBase.Capture();
        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, cap.Handler);
        sw.MarkHot(regionBase);
        cap.Chunks.Clear();

        // Advance past TTL without re-marking
        now += DisplaySweep.HotTtlMs + 1;
        sw.Tick(long.MaxValue, cap.Handler);

        // Should not re-offer the un-marked chunk
        Assert.Empty(cap.Chunks);
    }

    /// <summary>
    /// A hot chunk that IS re-marked before TTL expires must still be offered.
    /// </summary>
    [Fact]
    public void Hot_chunk_remarked_before_ttl_is_still_offered()
    {
        long regionBase = 0x31_0000_0000L;
        var heap = DisplaySweepTestBase.OneRegion(regionBase, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        var cap = new DisplaySweepTestBase.Capture();
        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, cap.Handler);
        sw.MarkHot(regionBase);
        cap.Chunks.Clear();

        // Advance past HotRescanMs but before TTL; re-mark the chunk
        now += DisplaySweep.HotRescanMs + 1;
        sw.MarkHot(regionBase);  // refreshes timestamp
        sw.Tick(long.MaxValue, cap.Handler);

        Assert.True(cap.Chunks.Count >= 1, "remarked chunk should still be offered");
    }
}
