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
    private readonly HashSet<long> _absent = new();
    public readonly List<long> WrittenAddrs = new();

    internal FakeSparseMemory Sparse => _sparse;

    /// <summary>LW-91 test seam: marks a heap region temporarily absent (a freed/reused buffer,
    /// or one that stops mapping mid-battle) without discarding its bytes. TryReadBytes/ReadInto/
    /// Writable/WriteBytes all fail against an absent region, the same way a genuinely unmapped
    /// address does; <see cref="RegionBytes"/> still reads the underlying bytes directly (a
    /// test's own point-in-time snapshot, never a live memory access) so a strike-retention test
    /// can assert a struck copy's content never changed while it was absent.</summary>
    internal void SetRegionPresent(long baseAddr, bool present)
    {
        if (present) _absent.Remove(baseAddr);
        else _absent.Add(baseAddr);
    }

    internal void AddHeapRegion(long baseAddr, byte[] data, bool writable = true)
    {
        _heap[baseAddr] = (byte[])data.Clone();
        if (writable) _writable.Add(baseAddr);
    }

    /// <summary>LW-31 stage 3: adds a genuine enc1 "Attack" table copy AND its adjacent 36-byte
    /// record (LabelAddr - AttackRow.RecordGap) in one call, mirroring how a real live copy always
    /// has both: the record is not an optional add-on, it is a structural part of the table. For
    /// enc==2, only the flat table is added (AttackRow's record mechanism is enc1-only; see
    /// AttackCard.Paint.cs's class doc for the dead-path rationale). Defaults to a VANILLA-shaped
    /// record over a flat vanilla-style desc string. Pass <paramref name="oursRowNameChars"/> to
    /// seed an already-painted "Ours" shape instead (tests exercising a mid-battle re-adoption or a
    /// stale-shape eviction): <paramref name="descText"/> is then split at that char boundary (the
    /// character there stands in for the split image's own NUL separator) and rebuilt as a REAL
    /// split image via AttackRow.Policy.BuildImage: a genuine "Ours" copy's footprint bytes are
    /// name+NUL+tail, never a flat "name tail" run with a space where the NUL belongs.</summary>
    internal void AddAttackTable(long baseAddr, int enc, string descText, int? oursRowNameChars = null)
    {
        if (oursRowNameChars.HasValue && enc == 1)
        {
            string rowName = descText.Substring(0, oursRowNameChars.Value);
            string tail = descText.Substring(oursRowNameChars.Value + 1);
            byte[] image = AttackRow.BuildImage(rowName, tail);
            AddHeapRegion(baseAddr, AttackCardTableFixture.BuildSplit(image));
            long oursRecordAddr = AttackCardTableFixture.Addrs(baseAddr, enc).labelAddr - AttackRow.RecordGap;
            AddHeapRegion(oursRecordAddr, AttackCardTableFixture.BuildOursRecord(oursRowNameChars.Value));
            return;
        }

        AddHeapRegion(baseAddr, AttackCardTableFixture.Build(enc, descText));
        if (enc != 1) return;
        var (labelAddr, _) = AttackCardTableFixture.Addrs(baseAddr, enc);
        long recordAddr = labelAddr - AttackRow.RecordGap;
        AddHeapRegion(recordAddr, AttackCardTableFixture.BuildVanillaRecord());
    }

    /// <summary>The record address for a table built at <paramref name="baseAddr"/> (enc1 only):
    /// LabelAddr - AttackRow.RecordGap.</summary>
    internal static long RecordAddrFor(long baseAddr, int enc)
    {
        var (labelAddr, _) = AttackCardTableFixture.Addrs(baseAddr, enc);
        return labelAddr - AttackRow.RecordGap;
    }

    /// <summary>Point-in-time copy of one region's current bytes, for assertions.</summary>
    internal byte[] RegionBytes(long baseAddr) => (byte[])_heap[baseAddr].Clone();

    private bool FindRegion(long addr, int len, out long baseAddr, out byte[] data)
    {
        foreach (var kv in _heap)
        {
            if (_absent.Contains(kv.Key)) continue;
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

    /// <summary>LW-31 stage-2: plants the condensed turn-queue struct (Offsets.TurnQueue:
    /// team/level/hp/maxHp) the cursor-first guard tests need, and marks the struct's base
    /// address Readable (FakeSparseMemory's Readable contract is an exact-address set, see its
    /// class doc). Unseeded is the default for every OTHER test in this file, so Readable() stays
    /// false there and the cursor path fails closed exactly like a genuine unreadable struct;
    /// every pre-existing test is unaffected.</summary>
    internal void SeatCursor(int team, int level, int hp, int maxHp)
    {
        _sparse.U16s[Offsets.TurnQueue + Offsets.TqTeam] = (ushort)team;
        _sparse.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
        _sparse.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        _sparse.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        _sparse.ReadableAddrs.Add(Offsets.TurnQueue);
    }

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

    /// <summary>Same packed layout as <see cref="Build"/>, but the desc segment is the RAW bytes of
    /// <paramref name="image"/> (already including its own embedded NUL + trailing pad) instead of
    /// an encoded text string; used to seed a genuine split-image footprint (an "Ours" shape's
    /// actual byte layout), enc1 only.</summary>
    internal static byte[] BuildSplit(byte[] image)
    {
        byte[] lead = new byte[PadBefore];
        byte[] label = ByteScan.Enc("Attack", 1);
        byte[] nul1 = new byte[1];
        byte[] trail = new byte[PadAfter];

        var parts = new[] { lead, label, nul1, image, trail };
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

    /// <summary>The 36-byte Attack-command record, vanilla-shaped (nameOff/descOff/poolOff/id =
    /// the census-proven {0x1FC1, 0x1FC8, 0x1FC0, 1}). Bytes past the first 16 (poolHead8Off, three
    /// zero fields, the per-command ordinal) are left zero: AttackRow.IsVanillaShape/IsOurShape
    /// never read them.</summary>
    internal static byte[] BuildVanillaRecord() =>
        BuildRecord(AttackRow.VanillaNameOff, AttackRow.VanillaDescOff, AttackRow.PoolHeadOff, AttackRow.RecordId);

    /// <summary>The 36-byte record already repointed at a split image whose row name is
    /// <paramref name="rowNameChars"/> chars long ("Ours" shape).</summary>
    internal static byte[] BuildOursRecord(int rowNameChars) =>
        BuildRecord(AttackRow.VanillaDescOff, AttackRow.VanillaDescOff + (uint)(rowNameChars + 1), AttackRow.PoolHeadOff, AttackRow.RecordId);

    /// <summary>A record matching neither shape: a freed/reused pool slot's leftover content.</summary>
    internal static byte[] BuildForeignRecord() => BuildRecord(0x9999, 0x1234, 0x1FC0, 1);

    private static byte[] BuildRecord(uint nameOff, uint descOff, uint poolOff, uint id)
    {
        var buf = new byte[AttackRow.RecordBytes];
        System.BitConverter.GetBytes(nameOff).CopyTo(buf, 0);
        System.BitConverter.GetBytes(descOff).CopyTo(buf, 4);
        System.BitConverter.GetBytes(poolOff).CopyTo(buf, 8);
        System.BitConverter.GetBytes(id).CopyTo(buf, 12);
        return buf;
    }
}
