using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// DisplaySweep multiple-region and zero-result-pass tests.
/// </summary>
public class DisplaySweepMultiRegionTests
{
    [Fact]
    public void Multiple_small_regions_all_walked_with_searchable_equal_to_region_size()
    {
        long base1 = 0xC_0000_0000L;
        long base2 = base1 + 0x10_0000L;
        long base3 = base2 + 0x10_0000L;
        int sz = 512;
        var heap = new FakeHeap(
            (base1, new byte[sz]),
            (base2, new byte[sz]),
            (base3, new byte[sz]));
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        sw.Tick(long.MaxValue, cap.Handler);

        var seen = new HashSet<long>();
        foreach (var (cs, _, s) in cap.Chunks)
        {
            Assert.Equal(sz, s);
            seen.Add(cs);
        }

        Assert.Contains(base1, seen);
        Assert.Contains(base2, seen);
        Assert.Contains(base3, seen);
    }

    [Fact]
    public void Zero_result_pass_does_not_restart_before_rest_gap()
    {
        var heap = DisplaySweepFixtures.OneRegion(0xD_0000_0000L, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        long gen0 = sw.Generation;
        int bgOffers = 0;

        sw.Tick(long.MaxValue, (_, _, _, _) => { });

        for (int i = 0; i < 20; i++)
        {
            now += 100;
            sw.Tick(long.MaxValue, (_, _, _, _) => bgOffers++);
        }

        Assert.Equal(0, bgOffers);
        Assert.Equal(gen0, sw.Generation);
    }
}
