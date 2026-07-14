using System;
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
///
/// Extended for the (now-retired) callout on-demand suites: the default IGameMemory.WriteBytes is
/// a silent no-op, which left a multi-byte write (e.g. a linger-arm dword) unobservable.
/// WriteBytes now records into WrittenBytes AND invokes the optional OnWrite hook so a test can
/// fold memory writes into the same ordered op log as native calls -- kept as a generic harness
/// for any future suite that needs write-order assertions, even with its original consumer gone.
/// </summary>
internal sealed class FakeSparseMemory : IGameMemory
{
    public readonly Dictionary<long, ushort> U16s = new();
    public readonly Dictionary<long, byte>   U8s  = new();
    public readonly HashSet<long> WritableAddrs   = new();
    public readonly Dictionary<long, byte>   Written = new();
    public readonly Dictionary<long, ushort> WrittenU16 = new();
    public readonly List<(long addr, byte[] bytes)> WrittenBytes = new();
    public Action<long, byte[]>? OnWrite;

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
    public void W16(long a, ushort v) { WrittenU16[a] = v; U16s[a] = v; }

    public void WriteBytes(long addr, byte[] data)
    {
        WrittenBytes.Add((addr, (byte[])data.Clone()));
        OnWrite?.Invoke(addr, data);
    }

    // U32 support: ArmAudit reads 4-byte PE fields as two U16 reads, or via U8x4.
    // We override TryReadBytes so the fingerprint path works, and expose U8 for U32
    // by splitting the stored uint into bytes.
    //
    // LW-82: serves any read FULLY CONTAINED in a registered block (base <= addr &&
    // addr + len <= base + block.Length), a strict superset of the original exact-base-only
    // semantics (base == addr is still served, as a 1-entry special case of "contained"). This
    // lets AnchorScoutTests stage one wide region and read arbitrary sub-windows out of it (a
    // chunked scan's reads rarely land exactly on a block's own base address), without changing
    // any existing exact-base caller's behavior.
    public bool TryReadBytes(long addr, int len, out byte[] buf)
    {
        buf = new byte[len];
        foreach (var pair in TerrainBlocks)
        {
            long @base = pair.Key;
            byte[] block = pair.Value;
            if (addr >= @base && addr + len <= @base + block.Length)
            {
                System.Array.Copy(block, addr - @base, buf, 0, len);
                return true;
            }
        }
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

    /// <summary>Seed a U64 value as 8 little-endian bytes at <paramref name="addr"/> (mirrors
    /// SeedU32) so IGameMemory's default U64 (eight composed U8 reads) returns the expected
    /// value. Used to seed Offsets.ActorPtr in TurnTracker/Iai tests.</summary>
    public void SeedU64(long addr, ulong value)
    {
        for (int i = 0; i < 8; i++)
            U8s[addr + i] = (byte)((value >> (i * 8)) & 0xFF);
    }
}
