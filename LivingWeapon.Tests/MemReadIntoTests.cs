using System;
using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Mem.ReadInto contract: read n bytes from an address into a caller-managed buffer,
/// returning n on success or 0 on a failed/wild address. Uses RPM internally, so fails
/// safely without an AV. Buffer size must be >= n; if buf.Length < n, return 0.
/// </summary>
public class MemReadIntoTests
{
    [Fact]
    public void Reads_into_pinned_buffer_from_valid_address()
    {
        // Pin a managed array in memory, read from it, and verify the content.
        var src = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var h = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            long addr = h.AddrOfPinnedObject().ToInt64();
            var dst = new byte[4];
            int n = Mem.ReadInto(addr, dst, 4);
            Assert.Equal(4, n);
            Assert.Equal(src, dst);
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Partial_read_returns_count()
    {
        var src = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var h = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            long addr = h.AddrOfPinnedObject().ToInt64();
            var dst = new byte[4];
            int n = Mem.ReadInto(addr, dst, 2);   // read only 2 bytes
            Assert.Equal(2, n);
            Assert.Equal(0xAA, dst[0]);
            Assert.Equal(0xBB, dst[1]);
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Wild_address_returns_zero()
    {
        var dst = new byte[4];
        int n = Mem.ReadInto(0x10, dst, 4);   // arbitrary invalid address
        Assert.Equal(0, n);
    }

    [Fact]
    public void Buffer_too_small_returns_zero()
    {
        var src = new byte[] { 0x12, 0x34 };
        var h = GCHandle.Alloc(src, GCHandleType.Pinned);
        try
        {
            long addr = h.AddrOfPinnedObject().ToInt64();
            var dst = new byte[1];   // too small for 2-byte read
            int n = Mem.ReadInto(addr, dst, 2);
            Assert.Equal(0, n);
        }
        finally { h.Free(); }
    }
}
