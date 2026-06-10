using System;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// FakeHeap contract: a region-backed IGameMemory fake. Reads/writes succeed only
/// within registered regions; cross-boundary and out-of-region access fail safely.
/// Regions are ordered, writes track a call counter, writability is enforced.
/// </summary>
public class FakeHeapTests
{
    [Fact]
    public void In_region_read_succeeds()
    {
        var data = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var heap = new FakeHeap((0x1000, data));
        Assert.Equal(0x12, heap.U8(0x1000));
        Assert.Equal(0x3412, heap.U16(0x1000));   // little-endian
    }

    [Fact]
    public void Out_of_region_read_returns_zero()
    {
        var data = new byte[] { 0x12, 0x34 };
        var heap = new FakeHeap((0x1000, data));
        Assert.Equal(0, heap.U8(0x2000));
        Assert.Equal(0, heap.U16(0x2000));
    }

    [Fact]
    public void Cross_boundary_u16_fails()
    {
        var data = new byte[] { 0x12, 0x34 };
        var heap = new FakeHeap((0x1000, data));
        // 0x1000 + 0x1001 is in-region, but 0x1001 + 0x1002 needs byte at 0x1002 (out)
        Assert.Equal(0, heap.U16(0x1001));   // needs 2 bytes starting at 0x1001
    }

    [Fact]
    public void Multiple_regions_read_independently()
    {
        var data1 = new byte[] { 0x11, 0x22 };
        var data2 = new byte[] { 0x33, 0x44 };
        var heap = new FakeHeap((0x1000, data1), (0x2000, data2));
        Assert.Equal(0x11, heap.U8(0x1000));
        Assert.Equal(0x33, heap.U8(0x2000));
    }

    [Fact]
    public void Try_read_bytes_in_region_succeeds()
    {
        var data = new byte[] { 0xAA, 0xBB, 0xCC };
        var heap = new FakeHeap((0x1000, data));
        bool ok = heap.TryReadBytes(0x1000, 3, out byte[] buf);
        Assert.True(ok);
        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, buf);
    }

    [Fact]
    public void Try_read_bytes_out_of_region_fails()
    {
        var data = new byte[] { 0xAA, 0xBB };
        var heap = new FakeHeap((0x1000, data));
        bool ok = heap.TryReadBytes(0x3000, 2, out byte[] buf);
        Assert.False(ok);
    }

    [Fact]
    public void Read_into_caller_buffer_fills_and_returns_count()
    {
        var data = new byte[] { 0x11, 0x22, 0x33 };
        var heap = new FakeHeap((0x1000, data));
        var buf = new byte[4];
        int n = heap.ReadInto(0x1000, buf, 3);
        Assert.Equal(3, n);
        Assert.Equal(0x11, buf[0]);
        Assert.Equal(0x22, buf[1]);
        Assert.Equal(0x33, buf[2]);
    }

    [Fact]
    public void Read_into_out_of_region_returns_zero()
    {
        var data = new byte[] { 0x11 };
        var heap = new FakeHeap((0x1000, data));
        var buf = new byte[2];
        int n = heap.ReadInto(0x2000, buf, 2);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Write_bytes_in_writable_region_succeeds_and_counts()
    {
        var data = new byte[4];
        var heap = new FakeHeap((0x1000, data, writable: true));
        heap.WriteBytes(0x1000, new byte[] { 0xAA, 0xBB });
        var regionData = heap.RegionBytes(0x1000);
        Assert.NotNull(regionData);
        Assert.Equal(0xAA, regionData![0]);
        Assert.Equal(0xBB, regionData![1]);
        Assert.Equal(1, heap.Writes);
    }

    [Fact]
    public void Write_bytes_in_readonly_region_is_no_op()
    {
        var data = new byte[4];
        var heap = new FakeHeap((0x1000, data, writable: false));
        heap.WriteBytes(0x1000, new byte[] { 0xAA, 0xBB });
        Assert.Equal(0, data[0]);
        Assert.Equal(0, data[1]);
        Assert.Equal(0, heap.Writes);   // write did not count
    }

    [Fact]
    public void Write_bytes_out_of_region_is_no_op()
    {
        var data = new byte[4];
        var heap = new FakeHeap((0x1000, data, writable: true));
        heap.WriteBytes(0x2000, new byte[] { 0xAA });
        Assert.Equal(0, heap.Writes);
    }

    [Fact]
    public void Readable_checks_region_bounds()
    {
        var data = new byte[10];
        var heap = new FakeHeap((0x1000, data));
        Assert.True(heap.Readable(0x1000, 5));
        Assert.False(heap.Readable(0x1000, 15));   // spans beyond region
        Assert.False(heap.Readable(0x2000, 1));    // outside all regions
    }

    [Fact]
    public void Writable_checks_region_bounds_and_writability()
    {
        var data1 = new byte[10];
        var data2 = new byte[10];
        var heap = new FakeHeap((0x1000, data1, writable: true), (0x2000, data2, writable: false));
        Assert.True(heap.Writable(0x1000, 5));
        Assert.False(heap.Writable(0x2000, 5));   // readonly region
        Assert.False(heap.Writable(0x3000, 1));   // outside all regions
    }

    [Fact]
    public void Regions_yields_in_ascending_order()
    {
        var heap = new FakeHeap((0x3000, new byte[2]), (0x1000, new byte[2]), (0x2000, new byte[2]));
        var regions = heap.Regions().ToList();
        Assert.Equal(3, regions.Count);
        Assert.Equal(0x1000, regions[0].baseAddr);
        Assert.Equal(0x2000, regions[1].baseAddr);
        Assert.Equal(0x3000, regions[2].baseAddr);
    }

    [Fact]
    public void Add_region_inserts_in_sorted_order()
    {
        var heap = new FakeHeap((0x2000, new byte[2]));
        heap.AddRegion(0x1000, new byte[2]);
        heap.AddRegion(0x3000, new byte[2]);
        var regions = heap.Regions().ToList();
        Assert.Equal(new[] { 0x1000L, 0x2000L, 0x3000L }, regions.Select(r => r.baseAddr).ToArray());
    }

    [Fact]
    public void Remove_region_deletes_by_address()
    {
        var heap = new FakeHeap((0x1000, new byte[2]), (0x2000, new byte[2]));
        heap.RemoveRegion(0x1000);
        var regions = heap.Regions().ToList();
        Assert.Single(regions);
        Assert.Equal(0x2000, regions[0].baseAddr);
    }

    [Fact]
    public void Region_bytes_retrieves_region_data()
    {
        var data = new byte[] { 0x11, 0x22, 0x33 };
        var heap = new FakeHeap((0x1000, data));
        var retrieved = heap.RegionBytes(0x1000);
        Assert.Equal(data, retrieved);
    }

    [Fact]
    public void Region_bytes_nonexistent_region_returns_null()
    {
        var heap = new FakeHeap((0x1000, new byte[2]));
        var retrieved = heap.RegionBytes(0x2000);
        Assert.Null(retrieved);
    }
}
