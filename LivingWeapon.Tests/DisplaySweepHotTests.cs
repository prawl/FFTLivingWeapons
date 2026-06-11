using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// DisplaySweep hot-chunk re-scan tests: clock-based and request-based rescan timing.
/// </summary>
public class DisplaySweepHotTests
{
    [Fact]
    public void Hot_before_rescan_interval_does_not_reoffer()
    {
        long regionBase = 0x5_0000_0000L;
        var heap = DisplaySweepFixtures.OneRegion(regionBase, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        DisplaySweepFixtures.DrainRegion(sw, ref now, cap, regionBase, DisplaySweep.ChunkSize);
        sw.MarkHot(regionBase);
        cap.Chunks.Clear();

        now += DisplaySweep.HotRescanMs - 1;
        sw.Tick(DisplaySweep.ChunkSize, cap.Handler);

        Assert.Empty(cap.Chunks);
    }

    [Fact]
    public void Hot_after_rescan_interval_reoffers_chunk()
    {
        long regionBase = 0x5_1000_0000L;
        var heap = DisplaySweepFixtures.OneRegion(regionBase, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        DisplaySweepFixtures.DrainRegion(sw, ref now, cap, regionBase, DisplaySweep.ChunkSize);
        sw.MarkHot(regionBase);
        cap.Chunks.Clear();

        now += DisplaySweep.HotRescanMs + 1;
        sw.Tick(DisplaySweep.ChunkSize, cap.Handler);

        Assert.True(cap.Chunks.Count >= 1, "hot chunk should be re-offered after HotRescanMs");
        Assert.Equal(regionBase, cap.Chunks[0].chunkStart);
    }

    [Fact]
    public void Hot_request_rescan_forces_hot_pass_before_clock()
    {
        long regionBase = 0x6_0000_0000L;
        var heap = DisplaySweepFixtures.OneRegion(regionBase, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        DisplaySweepFixtures.DrainRegion(sw, ref now, cap, regionBase, DisplaySweep.ChunkSize);
        sw.MarkHot(regionBase);
        cap.Chunks.Clear();

        now += 1;
        sw.RequestRescan();
        sw.Tick(DisplaySweep.ChunkSize, cap.Handler);

        Assert.True(cap.Chunks.Count >= 1, "RequestRescan must force a hot pass regardless of clock");
    }

    [Fact]
    public void Hot_chunk_removed_from_regions_is_dropped_after_failed_pass()
    {
        long regionBase = 0x7_0000_0000L;
        var heap = DisplaySweepFixtures.OneRegion(regionBase, DisplaySweep.ChunkSize);
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);
        var cap = new DisplaySweepFixtures.Capture();

        DisplaySweepFixtures.DrainRegion(sw, ref now, cap, regionBase, DisplaySweep.ChunkSize);
        sw.MarkHot(regionBase);

        heap.RemoveRegion(regionBase);

        cap.Chunks.Clear();
        now += DisplaySweep.HotRescanMs + 1;
        sw.Tick(DisplaySweep.ChunkSize, cap.Handler);

        Assert.Empty(cap.Chunks);

        now += DisplaySweep.HotRescanMs + 1;
        sw.Tick(DisplaySweep.ChunkSize, cap.Handler);
        Assert.Empty(cap.Chunks);
    }
}
