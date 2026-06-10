using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// DisplaySweep lookback tests: first chunk zero lookback, subsequent chunks have prefix.
/// </summary>
public class DisplaySweepLookbackTests
{
    [Fact]
    public void Lookback_first_chunk_of_region_has_zero_lookback()
    {
        long regionBase = 0x3_0000_0000L;
        var heap = DisplaySweepTestBase.OneRegion(regionBase, DisplaySweep.ChunkSize + 1024);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepTestBase.Capture();

        sw.Tick(long.MaxValue, cap.Handler);

        var first = cap.Chunks[0];
        Assert.Equal(0, first.lookback);
        Assert.Equal(regionBase, first.chunkStart);
    }

    [Fact]
    public void Lookback_second_chunk_has_Lookback_bytes_prefix_and_correct_base()
    {
        int chunkSize = DisplaySweep.ChunkSize;
        long regionBase = 0x4_0000_0000L;
        var heap = DisplaySweepTestBase.OneRegion(regionBase, chunkSize * 2 + 1024);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepTestBase.Capture();

        int limit = 20;
        while (cap.Chunks.Count < 2 && limit-- > 0)
            sw.Tick(long.MaxValue, cap.Handler);

        var second = cap.Chunks[1];
        int expectedLookback = System.Math.Min(DisplaySweep.Lookback, chunkSize);
        Assert.Equal(expectedLookback, second.lookback);
        Assert.Equal(regionBase + chunkSize, second.chunkStart);
    }
}
