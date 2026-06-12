using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Sparse address -&gt; value IGameMemory fake shared by the policy/tracker suites
/// (KillTracker, TurnTracker, Wielder, ExtraTurn, Rapture, ...). Unseeded reads
/// return 0, mirroring Mem's fail-safe contract. W8 records the write in Written
/// (so tests can assert exactly what was written) AND updates U8s so read-backs
/// observe it. Writable passes only for explicitly marked addresses -- the slam
/// guard's contract from the ExtraTurn integration suite.
///
/// Extended for TreasureMaster tests:
///   ReadableAddrs  -- Readable() returns true only for members (default: false).
///   TerrainBlocks  -- TryReadBytes serves a block registered here (keyed by base addr).
///   U32s           -- for PE header reads (U32 = two U16 reads combined).
///   ReadCount      -- counts how many times each address has been read via U8.
/// </summary>
internal sealed class FakeSparseMemory : IGameMemory
{
    public readonly Dictionary<long, ushort> U16s = new();
    public readonly Dictionary<long, byte>   U8s  = new();
    public readonly HashSet<long> WritableAddrs   = new();
    public readonly Dictionary<long, byte>   Written = new();

    // TreasureMaster extensions
    public readonly HashSet<long>             ReadableAddrs  = new();
    public readonly Dictionary<long, byte[]>  TerrainBlocks  = new();
    public readonly Dictionary<long, uint>    U32s           = new();
    public readonly Dictionary<long, int>     ReadCount      = new();

    public byte U8(long a)
    {
        ReadCount[a] = ReadCount.TryGetValue(a, out int c) ? c + 1 : 1;
        return U8s.TryGetValue(a, out var v) ? v : (byte)0;
    }

    public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;

    public bool Readable(long a, int n) => ReadableAddrs.Contains(a);
    public bool Writable(long a, int n) => WritableAddrs.Contains(a);
    public void W8(long a, byte v) { Written[a] = v; U8s[a] = v; }

    // U32 support: ArmAudit reads 4-byte PE fields as two U16 reads, or via U8x4.
    // We override TryReadBytes so the fingerprint path works, and expose U8 for U32
    // by splitting the stored uint into bytes.
    public bool TryReadBytes(long addr, int len, out byte[] buf)
    {
        // Serve a registered terrain block if addr matches exactly.
        if (TerrainBlocks.TryGetValue(addr, out var block) && len <= block.Length)
        {
            buf = new byte[len];
            System.Array.Copy(block, buf, len);
            return true;
        }
        buf = new byte[len];
        return false;
    }

    /// <summary>Seed a U32 value as 4 little-endian bytes at <paramref name="addr"/> so
    /// ArmAudit's four-byte PE reads return the expected value.</summary>
    public void SeedU32(long addr, uint value)
    {
        U8s[addr + 0] = (byte)(value        & 0xFF);
        U8s[addr + 1] = (byte)((value >> 8)  & 0xFF);
        U8s[addr + 2] = (byte)((value >> 16) & 0xFF);
        U8s[addr + 3] = (byte)((value >> 24) & 0xFF);
        ReadableAddrs.Add(addr);
        ReadableAddrs.Add(addr + 1);
        ReadableAddrs.Add(addr + 2);
        ReadableAddrs.Add(addr + 3);
    }
}
