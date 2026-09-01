using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;

namespace LivingWeapon.Tests;

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

    /// <summary>LW-381 test hook: counts calls to <see cref="Regions"/> itself (not the number of
    /// regions returned) -- lets a test pin how many full-memory passes a sweep actually made,
    /// independent of how many regions or targets were involved.</summary>
    public int RegionsCalls { get; private set; }

    /// <summary>LW-381 test hook: total calls to <see cref="TryReadBytes"/> + <see cref="ReadInto"/>
    /// -- lets a test prove NO byte read happened at all (beyond the region list itself), e.g. an
    /// in-battle sweep that aborts before its first chunk.</summary>
    public int ReadCalls { get; private set; }

    /// <summary>LW-381 test hook: when set and it returns non-null for (addr,len), stands in for
    /// <see cref="TryReadBytes"/>'s normal region lookup at that address -- a stateful closure can
    /// return different bytes on successive calls for the SAME address, simulating a targeted
    /// read whose classification result goes stale before a later re-read (a real TOCTOU window).
    /// Returning null falls through to the normal region-backed read.</summary>
    public Func<long, int, byte[]?>? ReadOverride;

    /// <summary>LW-381 test hook: when set, overrides <see cref="Writable"/> entirely, bypassing
    /// the per-region Writable flag -- simulates a region's protection changing between a sweep's
    /// snapshot and its later write attempt.</summary>
    public Func<long, int, bool>? WritableOverride;

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
        ReadCalls++;
        if (ReadOverride != null)
        {
            var over = ReadOverride(addr, len);
            if (over != null) { buf = over; return true; }
        }
        buf = new byte[len];
        var r = FindRegion(addr, len);
        if (r == null) return false;
        long offset = addr - r.BaseAddr;
        Array.Copy(r.Data, (int)offset, buf, 0, len);
        return true;
    }

    public int ReadInto(long addr, byte[] buf, int len)
    {
        ReadCalls++;
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

    public void W8(long addr, byte value) => WriteBytes(addr, new[] { value });

    public bool Readable(long addr, int len) => FindRegion(addr, len) != null;

    public bool Writable(long addr, int len)
    {
        if (WritableOverride != null) return WritableOverride(addr, len);
        var r = FindRegion(addr, len);
        return r != null && r.Writable;
    }

    /// <summary>Mirrors production Mem.Regions()'s own contract: committed/PRIVATE/writable
    /// only. A read-only region is never yielded (LW-37's PoolLocator relies on this: a
    /// located region is writable BY CONSTRUCTION because it can only ever come from here).</summary>
    public IEnumerable<(long baseAddr, long size)> Regions()
    {
        RegionsCalls++;
        return _regions.Where(r => r.Writable).Select(r => (r.BaseAddr, (long)r.Data.Length)).ToList();
    }

    public byte[]? RegionBytes(long baseAddr)
    {
        var r = _regions.FirstOrDefault(x => x.BaseAddr == baseAddr);
        return r?.Data;
    }
}
