using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A5: GenerationRestMs must be measured from generation COMPLETION, not start.
/// A slow pass that takes nearly GenerationRestMs must not restart immediately.
/// </summary>
public class DisplaySweepGenRestTests
{
    /// <summary>
    /// A generation that completes with the clock advanced almost to GenerationRestMs
    /// during the pass must not restart until GenerationRestMs AFTER completion.
    /// </summary>
    [Fact]
    public void Rest_measured_from_completion_not_start()
    {
        long regionBase = 0x50_0000_0000L;
        var heap = DisplaySweepTestBase.OneRegion(regionBase, DisplaySweep.ChunkSize * 2);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        long gen0 = sw.Generation;
        int bgOffers = 0;

        // Consume first chunk (generation in progress)
        sw.Tick(DisplaySweep.ChunkSize, (_, _, _, _) => bgOffers++);
        bgOffers = 0;

        // Advance the clock almost to GenerationRestMs before completing
        now = DisplaySweep.GenerationRestMs - 1;
        // Complete the generation
        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        bgOffers = 0;
        long genAtComplete = sw.Generation;

        // Immediately after completion: no restart allowed yet
        // (rest is measured from completion, so 1ms after completion = way short)
        now += 1;
        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        Assert.Equal(0, bgOffers);
        Assert.Equal(genAtComplete, sw.Generation);

        // Advance past GenerationRestMs from completion: restart allowed
        now += DisplaySweep.GenerationRestMs + 1;
        bgOffers = 0;
        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        Assert.True(bgOffers > 0, "Background pass must start after GenerationRestMs from completion");
        Assert.True(sw.Generation > genAtComplete, "Generation counter must increment");
    }
}
