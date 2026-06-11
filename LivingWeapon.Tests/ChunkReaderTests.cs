using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Direct tests for the sweep's chunk reads: lookback clamping at region start, the
/// snapshot-resolved hot read, miss handling, and the stale-tail clear (up to 512 bytes
/// past readLen are zeroed so a short read never leaks the previous longer read's bytes
/// into the scanner). DisplaySweepLookbackTests/DisplaySweepStaleBufferTests pin the same
/// behaviors end-to-end through DisplaySweep.Tick.
/// </summary>
public class ChunkReaderTests
{
    private const long BigBase   = 0xB0_0000_0000L;
    private const long ShortBase = 0xB1_0000_0000L;

    [Fact]
    public void Stale_tail_cleared_up_to_512_bytes_after_shorter_read()
    {
        // Long read first: two-chunk region of 0xAA; reading the SECOND chunk fills
        // lookback (4096) + ChunkSize of the shared buffer with 0xAA.
        int bigSize = ChunkReader.ChunkSize * 2;
        var big = new byte[bigSize];
        for (int i = 0; i < bigSize; i++) big[i] = 0xAA;

        // Short region second: 256 bytes of 0xBB.
        var small = new byte[256];
        for (int i = 0; i < small.Length; i++) small[i] = 0xBB;

        var heap = new FakeHeap((BigBase, big), (ShortBase, small));
        var reader = new ChunkReader(heap);
        reader.Snapshot();

        int got = reader.Read(BigBase + ChunkReader.ChunkSize, out int lb, out _);
        Assert.Equal(ChunkReader.Lookback, lb);
        Assert.True(got >= ChunkReader.ChunkSize, "long read must dirty the buffer");
        Assert.Equal(0xAA, reader.Buf[300]);   // dirtied where the short read's tail will be

        got = reader.Read(ShortBase, out lb, out int searchable);
        Assert.Equal(small.Length, got);
        Assert.Equal(0, lb);
        Assert.Equal(small.Length, searchable); // searchable clamped to the region size

        // The 512 bytes immediately past readLen must be zero, not 0xAA.
        for (int i = got; i < got + 512; i++)
            Assert.Equal(0, reader.Buf[i]);
    }

    [Fact]
    public void Lookback_clamped_to_distance_from_region_start()
    {
        var heap = new FakeHeap((BigBase, new byte[ChunkReader.ChunkSize + 1024]));
        var reader = new ChunkReader(heap);
        reader.Snapshot();

        int got = reader.ReadInRegion(BigBase + 100, BigBase, BigBase + ChunkReader.ChunkSize + 1024,
                                      out int lb, out _);
        Assert.True(got > 0);
        Assert.Equal(100, lb);
    }

    [Fact]
    public void Read_outside_any_snapshotted_region_returns_zero()
    {
        var heap = new FakeHeap((BigBase, new byte[512]));
        var reader = new ChunkReader(heap);
        reader.Snapshot();

        int got = reader.Read(0xDEAD_0000L, out int lb, out int searchable);

        Assert.Equal(0, got);
        Assert.Equal(0, lb);
        Assert.Equal(0, searchable);
    }

    [Fact]
    public void Snapshot_captures_current_regions_and_replaces_prior_set()
    {
        var heap = new FakeHeap((BigBase, new byte[512]));
        var reader = new ChunkReader(heap);
        reader.Snapshot();
        Assert.Equal(new List<(long, long)> { (BigBase, 512L) }, reader.Regions);

        heap.RemoveRegion(BigBase);
        heap.AddRegion(ShortBase, new byte[256]);
        reader.Snapshot();
        Assert.Equal(new List<(long, long)> { (ShortBase, 256L) }, reader.Regions);
    }
}
