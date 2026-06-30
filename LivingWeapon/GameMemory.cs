using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The slice of memory access the gameplay logic needs, behind an interface so the
/// logic (kill attribution, etc.) is unit-testable with a fake memory -- no live game.
/// LiveMemory is the production adapter over the RPM/WPM-backed <see cref="Mem"/>.
/// Extended with bulk-read and display-paint methods so the seam covers the display
/// paint path too (ByteScan, which needs region enumeration and bulk reads).
/// </summary>
internal interface IGameMemory
{
    byte U8(long addr);
    ushort U16(long addr);
    bool TryReadBytes(long addr, int len, out byte[] buf)
    {
        buf = new byte[len];
        return false;
    }
    /// <summary>Read len bytes, throwing on a failed/partial read (Mem.ReadBytes' contract;
    /// callers that scan catch it). Default routes through TryReadBytes so fakes inherit the
    /// same throw-on-failure shape; LiveMemory delegates to Mem.ReadBytes.</summary>
    byte[] ReadBytes(long addr, int len)
        => TryReadBytes(addr, len, out var buf) ? buf : throw new InvalidOperationException("ReadProcessMemory failed");
    int ReadInto(long addr, byte[] buf, int len) => 0;
    void WriteBytes(long addr, byte[] data) { }
    /// <summary>Write a single byte to <paramref name="addr"/>. Default no-op (test fakes
    /// override; LiveMemory delegates to Mem.W8).</summary>
    void W8(long addr, byte value) { }
    void W16(long addr, ushort value) { }
    bool Readable(long addr, int len) => false;
    bool Writable(long addr, int len) => false;
    IEnumerable<(long baseAddr, long size)> Regions() => Array.Empty<(long, long)>();
}

internal sealed class LiveMemory : IGameMemory
{
    public byte U8(long addr) => Mem.U8(addr);
    public ushort U16(long addr) => Mem.U16(addr);
    public bool TryReadBytes(long addr, int len, out byte[] buf) => Mem.TryReadBytes(addr, len, out buf);
    public byte[] ReadBytes(long addr, int len) => Mem.ReadBytes(addr, len);
    public int ReadInto(long addr, byte[] buf, int len) => Mem.ReadInto(addr, buf, len);
    public void WriteBytes(long addr, byte[] data) => Mem.WriteBytes(addr, data);
    public void W8(long addr, byte value) => Mem.W8(addr, value);
    public void W16(long addr, ushort value) => Mem.W16(addr, value);
    public bool Readable(long addr, int len) => Mem.Readable(addr, len);
    public bool Writable(long addr, int len) => Mem.Writable(addr, len);
    public IEnumerable<(long baseAddr, long size)> Regions() => Mem.Regions();
}
