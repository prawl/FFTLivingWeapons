using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ScanCursor, the pure region-ordering/cursor-resumption seam extracted from the LW-27/LW-31
/// dev spikes' incremental census walk (docs/TODO.md). A one-shot 512MB gulp per keypress
/// burned its whole budget scanning LOW addresses before ever reaching the HIGH addresses where
/// the real UI strings live, and the resulting multi-hundred-ms block on the shared Engine tick
/// thread was long enough to swallow a sibling spike's keypress edge entirely. This pure half
/// covers the descending-order walk and its cursor math; the #if LWDEV shell (HeaderSpike.cs /
/// AttackCardSpike.cs) is not visible to the test build, so only these static helpers are
/// exercised here.
/// </summary>
public class ScanCursorTests
{
    [Fact]
    public void SortDescending_orders_regions_by_base_address_highest_first()
    {
        var regions = new List<(long rbase, long rsize)>
        {
            (0x1000, 100),
            (0x5000_0000_0000, 200),
            (0x2000, 50),
        };

        var sorted = ScanCursor.SortDescending(regions);

        Assert.Equal(new List<(long, long)>
        {
            (0x5000_0000_0000, 200),
            (0x2000, 50),
            (0x1000, 100),
        }, sorted);
    }

    [Fact]
    public void Empty_region_list_starts_done()
    {
        var regions = new List<(long rbase, long rsize)>();

        var cursor = RegionCursor.AtStart(regions);

        Assert.True(cursor.Done);
    }

    [Fact]
    public void NextSlice_walks_a_single_region_across_multiple_calls_and_completes()
    {
        long regionSize = ChunkReader.ChunkSize * 3;
        var regions = new List<(long rbase, long rsize)> { (0x1000_0000, regionSize) };
        var cursor = RegionCursor.AtStart(regions);

        var all = new List<(long rbase, long rend, long chunkStart)>();
        int guard = 0;
        while (!cursor.Done && guard++ < 100)
            all.AddRange(ScanCursor.NextSlice(regions, ref cursor, ChunkReader.ChunkSize));

        Assert.Equal(3, all.Count);
        Assert.Equal(0x1000_0000, all[0].chunkStart);
        Assert.Equal(0x1000_0000 + ChunkReader.ChunkSize, all[1].chunkStart);
        Assert.Equal(0x1000_0000 + ChunkReader.ChunkSize * 2L, all[2].chunkStart);
        Assert.True(cursor.Done);
    }

    [Fact]
    public void NextSlice_after_completion_returns_empty_and_stays_done()
    {
        var regions = new List<(long rbase, long rsize)> { (0x2000_0000, ChunkReader.ChunkSize) };
        var cursor = RegionCursor.AtStart(regions);
        ScanCursor.NextSlice(regions, ref cursor, ChunkReader.ChunkSize);
        Assert.True(cursor.Done);

        var slice = ScanCursor.NextSlice(regions, ref cursor, ChunkReader.ChunkSize);

        Assert.Empty(slice);
        Assert.True(cursor.Done);
    }

    [Fact]
    public void NextSlice_respects_budget_by_stopping_once_the_budget_is_met()
    {
        var regions = new List<(long rbase, long rsize)> { (0x3000_0000, ChunkReader.ChunkSize * 5) };
        var cursor = RegionCursor.AtStart(regions);

        var slice = ScanCursor.NextSlice(regions, ref cursor, ChunkReader.ChunkSize * 2);

        Assert.Equal(2, slice.Count);
    }

    [Fact]
    public void NextSlice_always_makes_forward_progress_even_with_a_budget_smaller_than_one_chunk()
    {
        var regions = new List<(long rbase, long rsize)> { (0x4000_0000, ChunkReader.ChunkSize * 2) };
        var cursor = RegionCursor.AtStart(regions);

        var slice = ScanCursor.NextSlice(regions, ref cursor, 1);

        Assert.Single(slice);
        Assert.False(cursor.Done);
    }

    [Fact]
    public void NextSlice_crosses_from_one_region_into_the_next()
    {
        var regions = new List<(long rbase, long rsize)>
        {
            (0x6000_0000, ChunkReader.ChunkSize),
            (0x1000_0000, ChunkReader.ChunkSize * 2),
        };
        var cursor = RegionCursor.AtStart(regions);

        var slice = ScanCursor.NextSlice(regions, ref cursor, ChunkReader.ChunkSize * 2);

        Assert.Equal(2, slice.Count);
        Assert.Equal(0x6000_0000, slice[0].chunkStart);
        Assert.Equal(0x1000_0000, slice[1].chunkStart);
        Assert.False(cursor.Done);
    }
}
