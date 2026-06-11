using System;
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
        using var src = PinnedBuf.Of(4);
        new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(src.Bytes, 0);
        var dst = new byte[4];
        int n = Mem.ReadInto(src.Addr, dst, 4);
        Assert.Equal(4, n);
        Assert.Equal(src.Bytes, dst);
    }

    [Fact]
    public void Partial_read_returns_count()
    {
        using var src = PinnedBuf.Of(4);
        new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }.CopyTo(src.Bytes, 0);
        var dst = new byte[4];
        int n = Mem.ReadInto(src.Addr, dst, 2);   // read only 2 bytes
        Assert.Equal(2, n);
        Assert.Equal(0xAA, dst[0]);
        Assert.Equal(0xBB, dst[1]);
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
        using var src = PinnedBuf.Of(2);
        new byte[] { 0x12, 0x34 }.CopyTo(src.Bytes, 0);
        var dst = new byte[1];   // too small for 2-byte read
        int n = Mem.ReadInto(src.Addr, dst, 2);
        Assert.Equal(0, n);
    }

    // ---- IGameMemory.ReadBytes: the throwing bulk read (Mem.ReadBytes' contract on the seam) ----

    [Fact]
    public void GameMemory_ReadBytes_returns_the_bytes_from_a_valid_address()
    {
        using var src = PinnedBuf.Of(4);
        new byte[] { 0x12, 0x34, 0x56, 0x78 }.CopyTo(src.Bytes, 0);
        IGameMemory live = new LiveMemory();
        Assert.Equal(src.Bytes, live.ReadBytes(src.Addr, 4));
    }

    [Fact]
    public void GameMemory_ReadBytes_throws_on_a_failed_read_like_Mem()
    {
        // The default interface implementation routes through TryReadBytes, so a fake that
        // doesn't seed the address inherits Mem.ReadBytes' throw-on-failure shape.
        IGameMemory fake = new FakeSparseMemory();
        Assert.Throws<InvalidOperationException>(() => fake.ReadBytes(0x1000, 4));
        IGameMemory live = new LiveMemory();
        Assert.Throws<InvalidOperationException>(() => live.ReadBytes(0x10, 4));   // wild address
    }
}
