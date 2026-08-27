using System;
using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S1: the extended-items memory primitives. The live patcher is driven over a
/// GCHandle-pinned buffer (the LiveMemory precedent: the real VirtualProtect + RPM/WPM path, no
/// game), the allocator's placement arithmetic is pinned as pure cases, and the fakes are
/// checked against the contract the S2 suites lean on.</summary>
public class CodePatcherTests
{
    [Fact]
    public void Live_patcher_round_trips_a_write_through_a_pinned_buffer()
    {
        var buf = new byte[64];
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            long addr = (long)pin.AddrOfPinnedObject();
            var p = new LiveCodePatcher();
            Assert.True(p.TryWrite(addr + 8, new byte[] { 0xE9, 0x11, 0x22, 0x33, 0x44 }));
            Assert.True(p.TryRead(addr + 8, 5, out var back));
            Assert.Equal(new byte[] { 0xE9, 0x11, 0x22, 0x33, 0x44 }, back);
            Assert.Equal(0xE9, buf[8]);   // the managed view observed the same bytes
            Assert.Equal(0, buf[13]);     // nothing past the write moved
        }
        finally { pin.Free(); }
    }

    [Fact]
    public void Live_patcher_refuses_an_unmapped_address_instead_of_faulting()
    {
        var p = new LiveCodePatcher();
        Assert.False(p.TryRead(0x10, 4, out var bytes));
        Assert.Empty(bytes);
        Assert.False(p.TryWrite(0x10, new byte[] { 1 }));
        Assert.False(p.TryRead(0, 1, out _));
        Assert.False(p.TryWrite(0x140000000L, Array.Empty<byte>()));   // empty write is a refusal, not a no-op success
    }

    [Theory]
    // region far below the window: no candidate
    [InlineData(0x1000L, 0x100000L, 4096, 0x140000000L, false, 0L)]
    // region straddling the window's low edge: first aligned address at or above rangeMin
    [InlineData(0xC0000000L, 0x10000000L, 4096, 0x140000000L, true, 0xC0010000L)]
    // aligned region inside the window: its own base
    [InlineData(0x150000000L, 0x20000L, 4096, 0x140000000L, true, 0x150000000L)]
    // unaligned region base rounds UP to the 64 KB granule
    [InlineData(0x150001000L, 0x20000L, 4096, 0x140000000L, true, 0x150010000L)]
    // too small once aligned
    [InlineData(0x150001000L, 0xF000L, 4096, 0x140000000L, false, 0L)]
    // region entirely above the window
    [InlineData(0x1C0000000L, 0x100000L, 4096, 0x140000000L, false, 0L)]
    public void PickAllocBase_places_only_inside_the_window_on_a_granule(long regionBase, long regionSize, int size,
        long near, bool expectOk, long expectCandidate)
    {
        bool ok = LiveNearAllocator.PickAllocBase(regionBase, regionSize, size, near,
            LiveNearAllocator.SearchRange, LiveNearAllocator.AllocGranule, out long candidate);
        Assert.Equal(expectOk, ok);
        if (ok)
        {
            Assert.Equal(expectCandidate, candidate);
            Assert.Equal(0, candidate % LiveNearAllocator.AllocGranule);
            long delta = candidate - near;
            Assert.InRange(delta, int.MinValue, int.MaxValue);   // the whole point: rel32-reachable
        }
    }

    [Fact]
    public void Live_allocator_returns_an_executable_page_within_rel32_reach_of_the_image_base()
    {
        // The image base is fixed at 0x140000000 on this game; the test process is a different
        // image, but the window is 2 GB either side so a free region always exists on x64.
        long got = new LiveNearAllocator().Alloc(4096, 0x140000000L);
        Assert.NotEqual(0, got);
        Assert.InRange(got - 0x140000000L, int.MinValue, int.MaxValue);
        var p = new LiveCodePatcher();
        Assert.True(p.TryWrite(got, new byte[] { 0xC3 }));   // the page is writable (and executable by allocation)
        Assert.True(p.TryRead(got, 1, out var b) && b[0] == 0xC3);
    }

    [Fact]
    public void Fake_patcher_refuses_unseeded_reads_and_records_writes_in_order()
    {
        var f = new FakeCodePatcher();
        Assert.False(f.TryRead(0x1000, 2, out _));
        f.Seed(0x1000, 0xAA, 0xBB);
        Assert.True(f.TryRead(0x1000, 2, out var b));
        Assert.Equal(new byte[] { 0xAA, 0xBB }, b);
        Assert.False(f.TryRead(0x1000, 3, out _));   // one byte past the seed is unreadable
        f.ZeroFillUnseeded = true;
        Assert.True(f.TryRead(0x1000, 3, out var z) && z[2] == 0);
        Assert.True(f.TryWrite(0x1001, new byte[] { 0xCC }));
        Assert.Equal(0xCC, f.Bytes[0x1001]);
        f.RefuseWritesAt.Add(0x2000);
        Assert.False(f.TryWrite(0x2000, new byte[] { 1 }));
        Assert.Single(f.Writes);
        Assert.Equal(0x1001, f.Writes[0].Addr);
    }

    [Fact]
    public void Fake_allocator_hands_out_distinct_in_window_pages_and_can_refuse()
    {
        var a = new FakeNearAllocator { RefuseAfter = 2 };
        long first = a.Alloc(4096, 0x140000000L), second = a.Alloc(8192, 0x140000000L);
        Assert.NotEqual(first, second);
        Assert.InRange(second - 0x140000000L, int.MinValue, int.MaxValue);
        Assert.Equal(0, a.Alloc(4096, 0x140000000L));
        Assert.Equal(3, a.Requests.Count);
    }
}
