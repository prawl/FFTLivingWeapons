using System;
using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Dictionary-backed <see cref="ICodePatcher"/> for the extended-inventory suites (LW-346). Unseeded
/// bytes read as unreadable (TryRead false) unless <see cref="ZeroFillUnseeded"/> is set, which
/// mirrors a zero-filled VirtualAlloc page; every write is applied AND appended to
/// <see cref="Writes"/> in order so a test can assert exactly what landed where (the rollback
/// suites depend on the order). <see cref="RefuseWritesAt"/> makes a chosen address refuse
/// (TryWrite false) to exercise the failure branches.
/// </summary>
internal sealed class FakeCodePatcher : ICodePatcher
{
    public readonly Dictionary<long, byte> Bytes = new();
    public readonly List<(long Addr, byte[] Data)> Writes = new();
    public readonly HashSet<long> RefuseWritesAt = new();
    public bool ZeroFillUnseeded;

    public void Seed(long addr, params byte[] data)
    {
        for (int i = 0; i < data.Length; i++) Bytes[addr + i] = data[i];
    }

    public bool TryRead(long address, int count, out byte[] bytes)
    {
        bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            if (Bytes.TryGetValue(address + i, out var b)) bytes[i] = b;
            else if (ZeroFillUnseeded) bytes[i] = 0;
            else { bytes = Array.Empty<byte>(); return false; }
        }
        return true;
    }

    public bool TryWrite(long address, byte[] data)
    {
        if (RefuseWritesAt.Contains(address)) return false;
        for (int i = 0; i < data.Length; i++) Bytes[address + i] = data[i];
        Writes.Add((address, (byte[])data.Clone()));
        return true;
    }

    public byte[] Read(long addr, int count)
    {
        var ok = TryRead(addr, count, out var b);
        if (!ok) throw new InvalidOperationException($"unseeded read at 0x{addr:X}");
        return b;
    }
}

/// <summary>Deterministic <see cref="INearAllocator"/>: hands out consecutive 64 KB-spaced
/// addresses starting at <see cref="Base"/> (default 0x150000000, inside +-2 GB of the image),
/// records every request, and can refuse (Alloc 0) after <see cref="RefuseAfter"/> grants.</summary>
internal sealed class FakeNearAllocator : INearAllocator
{
    public long Base = 0x150000000L;
    public int RefuseAfter = int.MaxValue;
    public readonly List<(int Size, long Near, long Got)> Requests = new();
    private long _next;

    public long Alloc(int size, long nearAddr)
    {
        if (Requests.Count >= RefuseAfter) { Requests.Add((size, nearAddr, 0)); return 0; }
        if (_next == 0) _next = Base;
        long got = _next;
        _next += 0x10000;
        Requests.Add((size, nearAddr, got));
        return got;
    }
}
