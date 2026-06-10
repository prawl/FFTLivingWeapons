using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// DisplaySweep generation pacing tests: rest gap, min gap, invalidate floor.
/// </summary>
public class DisplaySweepGenerationTests
{
    [Fact]
    public void Generation_no_background_rewalk_until_rest_gap_elapses()
    {
        var heap = DisplaySweepTestBase.OneRegion(0x8_0000_0000L, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        int bgOffers = 0;

        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        bgOffers = 0;

        now += DisplaySweep.GenerationRestMs / 2;
        for (int i = 0; i < 5; i++)
        {
            sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
            now += 10;
        }

        Assert.Equal(0, bgOffers);
    }

    [Fact]
    public void Generation_new_pass_starts_after_rest_gap()
    {
        var heap = DisplaySweepTestBase.OneRegion(0x9_0000_0000L, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        long gen0 = sw.Generation;
        int bgOffers = 0;

        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        Assert.True(bgOffers > 0);
        bgOffers = 0;

        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);

        Assert.True(bgOffers > 0, "new generation should produce background offers");
        Assert.True(sw.Generation > gen0, "generation should have incremented for the new pass");
    }

    [Fact]
    public void Invalidate_starts_new_pass_after_min_gap_not_before()
    {
        var heap = DisplaySweepTestBase.OneRegion(0xA_0000_0000L, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        long gen0 = sw.Generation;
        int bgOffers = 0;

        sw.Tick(long.MaxValue, (_, _, _, _) => { });

        sw.Invalidate();

        now = DisplaySweep.GenerationMinGapMs / 2;
        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        Assert.Equal(0, bgOffers);

        now = DisplaySweep.GenerationMinGapMs + 1;
        bgOffers = 0;
        sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        Assert.True(bgOffers > 0, "Invalidate should allow new pass once floor has elapsed");
        Assert.True(sw.Generation > gen0, "generation should increment for the Invalidate-triggered pass");
    }

    [Fact]
    public void Generation_counter_increments_per_pass_start()
    {
        var heap = DisplaySweepTestBase.OneRegion(0xB_0000_0000L, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        long gen0 = sw.Generation;

        sw.Tick(long.MaxValue, (_, _, _, _) => { });

        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, (_, _, _, _) => { });
        long gen1 = sw.Generation;
        Assert.True(gen1 > gen0, "generation should have incremented after second pass start");

        now += DisplaySweep.GenerationRestMs + 1;
        sw.Tick(long.MaxValue, (_, _, _, _) => { });
        Assert.True(sw.Generation > gen1, "generation should increment again for third pass");
    }
}
