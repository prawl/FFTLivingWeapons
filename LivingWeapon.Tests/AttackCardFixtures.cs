using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCard's runtime touches TWO disjoint concerns of IGameMemory, never overlapping in
/// address space or method surface: (1) the turn-owner seam (ActorRegister/ActorResolver read
/// only U8/U16/U64/Readable, at the real production Offsets (exactly what MemSeats + FakeSparseMemory
/// already serve every other suite)), and (2) the Attack-menu table copies themselves
/// (TryReadBytes/ReadInto/WriteBytes/Writable/Regions, at synthetic heap addresses this fixture
/// owns). This fixture composes a FakeSparseMemory (exposed as <see cref="Sparse"/> for MemSeats
/// calls) for concern 1, and its own region dictionary for concern 2, so a census's ChunkReader
/// walk and a roster/band read can never collide.
/// </summary>
internal sealed class AttackCardMemory : IGameMemory
{
    private readonly FakeSparseMemory _sparse = new();
    private readonly Dictionary<long, byte[]> _heap = new();
    private readonly HashSet<long> _writable = new();
    public readonly List<long> WrittenAddrs = new();

    internal FakeSparseMemory Sparse => _sparse;

    internal void AddHeapRegion(long baseAddr, byte[] data, bool writable = true)
    {
        _heap[baseAddr] = (byte[])data.Clone();
        if (writable) _writable.Add(baseAddr);
    }

    /// <summary>Point-in-time copy of one region's current bytes, for assertions.</summary>
    internal byte[] RegionBytes(long baseAddr) => (byte[])_heap[baseAddr].Clone();

    private bool FindRegion(long addr, int len, out long baseAddr, out byte[] data)
    {
        foreach (var kv in _heap)
        {
            if (addr >= kv.Key && addr + len <= kv.Key + kv.Value.Length)
            {
                baseAddr = kv.Key;
                data = kv.Value;
                return true;
            }
        }
        baseAddr = 0;
        data = Array.Empty<byte>();
        return false;
    }

    // --- concern 1: turn-owner seam, delegated straight to the inner FakeSparseMemory ---
    public byte U8(long addr) => _sparse.U8(addr);
    public ushort U16(long addr) => _sparse.U16(addr);
    public bool Readable(long addr, int len) => _sparse.Readable(addr, len);

    // --- concern 2: the Attack-menu table copies ---
    public bool TryReadBytes(long addr, int len, out byte[] buf)
    {
        buf = new byte[len];
        if (!FindRegion(addr, len, out long baseAddr, out byte[] data)) return false;
        Array.Copy(data, (int)(addr - baseAddr), buf, 0, len);
        return true;
    }

    public int ReadInto(long addr, byte[] buf, int len)
    {
        if (!FindRegion(addr, len, out long baseAddr, out byte[] data)) return 0;
        Array.Copy(data, (int)(addr - baseAddr), buf, 0, len);
        return len;
    }

    public void WriteBytes(long addr, byte[] payload)
    {
        WrittenAddrs.Add(addr);
        if (!FindRegion(addr, payload.Length, out long baseAddr, out byte[] data)) return;
        if (!_writable.Contains(baseAddr)) return;
        Array.Copy(payload, 0, data, (int)(addr - baseAddr), payload.Length);
    }

    public bool Writable(long addr, int len) => FindRegion(addr, len, out long baseAddr, out _) && _writable.Contains(baseAddr);

    public IEnumerable<(long baseAddr, long size)> Regions() => _heap.Select(kv => (kv.Key, (long)kv.Value.Length));
}

/// <summary>Builds the packed "Attack" table byte layout AttackCardSpike's census proved:
/// a standalone C-string "Attack" immediately followed (past its own NUL) by the desc string,
/// itself NUL-terminated. <paramref name="padBefore"/> bytes of leading zero (any multiple of
/// enc) satisfy the "preceded by NUL" standalone-hit rule without sitting at buffer offset 0.</summary>
internal static class AttackCardTableFixture
{
    internal const int PadBefore = 8;
    internal const int PadAfter = 32;

    internal static byte[] Build(int enc, string descText)
    {
        byte[] lead = new byte[PadBefore];
        byte[] label = ByteScan.Enc("Attack", enc);
        byte[] nul1 = new byte[enc];
        byte[] desc = ByteScan.Enc(descText, enc);
        byte[] nul2 = new byte[enc];
        byte[] trail = new byte[PadAfter];

        var parts = new[] { lead, label, nul1, desc, nul2, trail };
        int total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        int at = 0;
        foreach (var p in parts) { Array.Copy(p, 0, buf, at, p.Length); at += p.Length; }
        return buf;
    }

    /// <summary>The label address and desc address for a table built by <see cref="Build"/> at
    /// <paramref name="baseAddr"/>.</summary>
    internal static (long labelAddr, long descAddr) Addrs(long baseAddr, int enc)
    {
        long labelAddr = baseAddr + PadBefore;
        long descAddr = baseAddr + AttackCardProbeText.DescStart(PadBefore, enc);
        return (labelAddr, descAddr);
    }
}
