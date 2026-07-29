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
///   ReadCount      -- counts how many times each address has been read via U8.
///
/// LW-147 (DOCUMENTED DIVERGENCE from production -- read this before trusting a passing test):
/// production Mem.Probe (Mem.cs) gates the WHOLE [addr, addr+len) range against one committed
/// VirtualQuery region -- a multi-byte read/write over a partially-mapped region correctly
/// refuses. By DEFAULT this fake does NOT reproduce that: Readable(a, n)/Writable(a, n) still
/// check only base-address set membership and IGNORE n, exactly as before LW-147, because
/// MEASURING the honest fix (require every byte in the range to be individually marked) turned
/// on by default fails 94 pre-existing TESTS across 21 suites that mark only a struct's base
/// offset (e.g. `ReadableAddrs.Add(addr + Offsets.AMaxHp)` for a field production reads with
/// n=2) -- a different count from the 226 marking call sites across 35 files this touches --
/// past this pass's reconciliation budget. Reconciling all of them was OUT OF SCOPE for this
/// pass; the gap is real and stays open, tracked as LW-151 (docs/TODO.md) for closing it for
/// real (a test that marks only a field's base byte cannot tell a genuine multi-byte gate
/// refusal from a pass).
///
/// StrictRangeChecks (default false) is the opt-in escape hatch: when true, Readable/Writable
/// require EVERY byte in [a, a+n) to be an individually-marked member of ReadableAddrs/
/// WritableAddrs (mirroring Mem.Probe's real range gate) -- two adjacent single-byte Add calls
/// compose into full coverage of a 2-byte read exactly as one MarkReadable(a, 2) call would. Only
/// FakeSparseMemoryTests' own LW-147 pinning tests run strict today (proving the mechanism is
/// real, non-vacuous); a new suite that wants the honest range gate should opt in explicitly
/// rather than assume it is the default. The plain single-address `.Add(x)` path every
/// pre-existing call site uses keeps working unchanged in BOTH modes (it marks a 1-byte range);
/// MarkReadable(addr, len)/MarkWritable(addr, len) mark a longer run in one call for strict-mode
/// call sites staging a multi-byte production gate.
///
/// Extended for the (now-retired) callout on-demand suites: the default IGameMemory.WriteBytes is
/// a silent no-op, which left a multi-byte write (e.g. a linger-arm dword) unobservable.
/// WriteBytes now records into WrittenBytes AND invokes the optional OnWrite hook so a test can
/// fold memory writes into the same ordered op log as native calls -- kept as a generic harness
/// for any future suite that needs write-order assertions, even with its original consumer gone.
///
/// LW-145 fix 0 (the LW-147 slice this batch rides in on): W16 and WriteBytes used to log a write
/// (WrittenU16 / WrittenBytes) WITHOUT applying it, so a read-back after either one silently
/// returned the pre-write value -- the opposite of W8's own documented contract -- and neither
/// call appended to WriteOrder, so a "this address was never written" assertion built on
/// WriteOrder alone could miss a real W16/WriteBytes write. Fixed, but the three channels are NOT
/// symmetric -- read the exact mapping before trusting a read-back:
///   W8         -&gt; U8s only.
///   W16        -&gt; U16s, AND mirrors its low/high bytes into U8s (little-endian), so a U8
///                 read-back also observes it.
///   WriteBytes -&gt; U8s (every byte written), plus a containing TerrainBlocks block when one
///                 fully covers the range (mirroring TryReadBytes' own containment check) --
///                 but NEVER U16s. A U16() read-back after a 2-byte WriteBytes at that address
///                 still returns the PRE-write value (PlagueLevelDriftTests.cs documents this
///                 exact gap; observe such a write via OnWrite/WrittenBytes instead of U16()).
/// All three append to WriteOrder. Written/WrittenU16/WrittenBytes are unchanged (existing tests
/// read them).
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

    /// <summary>Every W8 call's address, in call order (duplicates kept). Written is a Dictionary
    /// (last-value-wins, no order), which cannot answer "which byte got written first" -- added for
    /// ProvokeTests' write-order assertion (inflict-row bytes before the action byte). Additive only:
    /// existing tests that only read Written/U8s are unaffected.</summary>
    public readonly List<long> WriteOrder = new();

    /// <summary>Opt-in switch to the honest length-aware Readable/Writable gate (see the class
    /// doc's LW-147 section). Defaults false: EVERY pre-existing call site in this repo was
    /// written against the legacy base-address-only semantics, so flipping the default would
    /// break 94 pre-existing tests across 21 suites (measured; a different count from the 226
    /// marking call sites across 35 files that mark only a struct field's base offset). Closing
    /// that gap for real is tracked as LW-151 (docs/TODO.md); this pass only documents and pins
    /// the divergence. Set true on a FakeSparseMemory instance a new/updated test controls to
    /// get the real per-byte range gate.</summary>
    public bool StrictRangeChecks;

    // TreasureMaster extensions
    public readonly HashSet<long>             ReadableAddrs  = new();
    public readonly Dictionary<long, byte[]>  TerrainBlocks  = new();
    public readonly Dictionary<long, int>     ReadCount      = new();

    public byte U8(long a)
    {
        ReadCount[a] = ReadCount.TryGetValue(a, out int c) ? c + 1 : 1;
        return U8s.TryGetValue(a, out var v) ? v : (byte)0;
    }

    public ushort U16(long a) => U16s.TryGetValue(a, out var v) ? v : (ushort)0;

    /// <summary>Default (StrictRangeChecks == false): legacy behavior, base-address membership
    /// only, n ignored -- see the class doc's LW-147 section for why. StrictRangeChecks == true:
    /// true only when EVERY byte in [a, a+n) has been individually marked in ReadableAddrs --
    /// mirrors production Mem.Probe, which gates the whole range against one committed region
    /// (see Mem.cs: addr &gt;= base &amp;&amp; addr + len &lt;= base + regionSize), not just the base address.
    /// Two adjacent single-byte Add calls compose into coverage of a 2-byte read exactly as
    /// MarkReadable(a, 2) would, because coverage is checked per byte.</summary>
    public bool Readable(long a, int n)
    {
        if (!StrictRangeChecks) return ReadableAddrs.Contains(a);
        for (int i = 0; i < n; i++)
            if (!ReadableAddrs.Contains(a + i)) return false;
        return true;
    }

    /// <summary>Writable's twin of Readable -- see its doc for the default-vs-strict rule.</summary>
    public bool Writable(long a, int n)
    {
        if (!StrictRangeChecks) return WritableAddrs.Contains(a);
        for (int i = 0; i < n; i++)
            if (!WritableAddrs.Contains(a + i)) return false;
        return true;
    }

    /// <summary>Mark [addr, addr+len) readable in one call -- equivalent to calling
    /// ReadableAddrs.Add for each byte in the range individually. Use this (rather than a
    /// manual loop or a single Add at the base address) when staging a multi-byte production
    /// gate, e.g. GrowthEngine.Locate's StructSpan read or ArmAudit's 4-byte PE field reads --
    /// marking only the base address leaves the rest of the range uncovered and Readable(addr,
    /// len) now correctly refuses.</summary>
    public void MarkReadable(long addr, int len) { for (int i = 0; i < len; i++) ReadableAddrs.Add(addr + i); }

    /// <summary>WritableAddrs' twin of MarkReadable.</summary>
    public void MarkWritable(long addr, int len) { for (int i = 0; i < len; i++) WritableAddrs.Add(addr + i); }
    public void W8(long a, byte v) { Written[a] = v; U8s[a] = v; WriteOrder.Add(a); }

    public void W16(long a, ushort v)
    {
        WrittenU16[a] = v;
        U16s[a] = v;
        U8s[a] = (byte)(v & 0xFF);
        U8s[a + 1] = (byte)((v >> 8) & 0xFF);
        WriteOrder.Add(a);
    }

    public void WriteBytes(long addr, byte[] data)
    {
        WrittenBytes.Add((addr, (byte[])data.Clone()));
        for (int i = 0; i < data.Length; i++) U8s[addr + i] = data[i];
        foreach (var pair in TerrainBlocks)
        {
            long @base = pair.Key;
            byte[] block = pair.Value;
            if (addr >= @base && addr + data.Length <= @base + block.Length)
            {
                System.Array.Copy(data, 0, block, addr - @base, data.Length);
                break;
            }
        }
        WriteOrder.Add(addr);
        OnWrite?.Invoke(addr, data);
    }

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
    /// ArmAudit's four-byte PE reads (ReadU32: four composed U8 reads) return the expected
    /// value, and marks all 4 bytes readable. Serves U8-COMPOSED readers ONLY: it writes U8s,
    /// not U16s, so IGameMemory's interface-default U32(addr) -- which composes from two U16
    /// reads (U16(addr) | U16(addr+2)&lt;&lt;16), see GameMemory.cs -- will NOT see a value seeded
    /// here. No current test calls the interface-default U32 through this fake; if one starts
    /// to, seed U16s directly (or add a U16-composed sibling) instead of assuming this helper
    /// covers it.</summary>
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
