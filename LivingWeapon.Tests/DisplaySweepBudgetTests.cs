using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// DisplaySweep budget tests: one-chunk minimum progress, no walking beyond budget.
/// </summary>
public class DisplaySweepBudgetTests
{
    [Fact]
    public void Budget_limits_first_tick_to_roughly_one_chunk()
    {
        var heap = DisplaySweepFixtures.OneRegion(0x1_0000_0000L, 12 * 1024 * 1024);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        sw.Tick(DisplaySweep.ChunkSize, cap.Handler);

        Assert.True(cap.Chunks.Count >= 1, "at least one chunk must be offered");
        long totalSearchable = 0;
        foreach (var (_, _, s) in cap.Chunks) totalSearchable += s;
        Assert.True(totalSearchable <= (long)DisplaySweep.ChunkSize * 2,
            $"budget tick walked too far: {totalSearchable}");
    }

    [Fact]
    public void Budget_consecutive_ticks_cover_region_search_windows_exactly_once()
    {
        int regionSize = 10 * 1024 * 1024;
        long regionBase = 0x2_0000_0000L;
        var heap = DisplaySweepFixtures.OneRegion(regionBase, regionSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        DisplaySweepFixtures.DrainRegion(sw, ref now, cap, regionBase, regionSize);

        var windows = new System.Collections.Generic.List<(long start, long end)>();
        foreach (var (cs, _, s) in cap.Chunks)
            if (cs >= regionBase && cs < regionBase + regionSize)
                windows.Add((cs, cs + s));

        windows.Sort((a, b) => a.start.CompareTo(b.start));
        long prev = regionBase;
        long covered = 0;
        foreach (var (start, end) in windows)
        {
            Assert.True(start >= prev, $"gap or overlap: prev={prev:X} start={start:X}");
            covered += end - start;
            prev = end;
        }
        Assert.Equal(regionSize, covered);
    }
}
