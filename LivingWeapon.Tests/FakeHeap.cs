using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// A region-backed IGameMemory fake for display tests. Manages multiple in-memory
/// regions (address-range pairs) with configurable writability. Reads/writes succeed
/// only within a single region; cross-boundary or out-of-region access fails safely.
/// TryReadBytes and ReadInto resolve within a SINGLE region; U8/U16 use ReadInto.
/// </summary>
internal sealed class FakeHeap : IGameMemory
{
    private sealed class Region
    {
        public long BaseAddr { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public bool Writable { get; set; }
    }

    private List<Region> _regions = new();
    public int Writes { get; private set; }

    public FakeHeap(params (long baseAddr, byte[] data)[] regions)
    {
        foreach (var (addr, data) in regions)
            AddRegion(addr, (byte[])data.Clone(), writable: true);
    }

    public FakeHeap(params (long baseAddr, byte[] data, bool writable)[] regions)
    {
        foreach (var (addr, data, writable) in regions)
            AddRegion(addr, (byte[])data.Clone(), writable);
    }

    public void AddRegion(long baseAddr, byte[] data, bool writable = true)
    {
        _regions.Add(new Region { BaseAddr = baseAddr, Data = (byte[])data.Clone(), Writable = writable });
        _regions = _regions.OrderBy(r => r.BaseAddr).ToList();
    }

    public void RemoveRegion(long baseAddr)
    {
        _regions.RemoveAll(r => r.BaseAddr == baseAddr);
    }

    private Region? FindRegion(long addr, int len)
    {
        var r = _regions.FirstOrDefault(x => addr >= x.BaseAddr && addr + len <= x.BaseAddr + x.Data.Length);
        return r;
    }

    public byte U8(long addr)
    {
        var buf = new byte[1];
        return ReadInto(addr, buf, 1) == 1 ? buf[0] : (byte)0;
    }

    public ushort U16(long addr)
    {
        var buf = new byte[2];
        if (ReadInto(addr, buf, 2) != 2) return 0;
        return (ushort)(buf[0] | (buf[1] << 8));
    }

    public bool TryReadBytes(long addr, int len, out byte[] buf)
    {
        buf = new byte[len];
        var r = FindRegion(addr, len);
        if (r == null) return false;
        long offset = addr - r.BaseAddr;
        Array.Copy(r.Data, (int)offset, buf, 0, len);
        return true;
    }

    public int ReadInto(long addr, byte[] buf, int len)
    {
        if (len > buf.Length) return 0;
        var r = FindRegion(addr, len);
        if (r == null) return 0;
        long offset = addr - r.BaseAddr;
        Array.Copy(r.Data, (int)offset, buf, 0, len);
        return len;
    }

    public void WriteBytes(long addr, byte[] data)
    {
        var r = FindRegion(addr, data.Length);
        if (r == null || !r.Writable) return;
        long offset = addr - r.BaseAddr;
        Array.Copy(data, 0, r.Data, (int)offset, data.Length);
        Writes++;
    }

    public bool Readable(long addr, int len) => FindRegion(addr, len) != null;

    public bool Writable(long addr, int len)
    {
        var r = FindRegion(addr, len);
        return r != null && r.Writable;
    }

    public IEnumerable<(long baseAddr, long size)> Regions()
        => _regions.Select(r => (r.BaseAddr, (long)r.Data.Length));

    public byte[]? RegionBytes(long baseAddr)
    {
        var r = _regions.FirstOrDefault(x => x.BaseAddr == baseAddr);
        return r?.Data;
    }
}
