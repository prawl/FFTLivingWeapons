using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Chunked reads for the display sweep, one shared buffer (lookback prefix + chunk + trailing slack)
/// filled from a per-Tick region snapshot so a hot pass avoids a VirtualQueryEx walk per chunk.
/// Both sweep phases read through here.
/// </summary>
internal sealed class ChunkReader
{
    public const int ChunkSize = 4 * 1024 * 1024;
    public const int Lookback = 4096;

    /// <summary>Trailing slack past a chunk's searchable window, for anchors that straddle a chunk boundary forward
    /// (CardScanner finds a weapon's flavor after its "Kills: " hit, the Reliquary Phase-2 equip-meter layout).
    /// Longest living flavor is 152 chars / 304 UTF-16 bytes (Excalibur id35) and a hit at the window end can need
    /// ~344 bytes past chunk end, so 4096 (== Lookback) covers it for ~4KB on a 4MB buffer. Chunk boundaries are fixed
    /// per region, so too little slack drops that card's site every pass. See CardPatterns.FitsTrailSlack and Display's ctor.</summary>
    public const int TrailSlack = 4096;
    /// <summary>Bytes zero-cleared past a short read so scans never see a stale tail. Pinned by IconCacheHideTests:
    /// the icon cache scan reaches at most PrefixMax + CStringMax past its window, and that sum must stay under this.</summary>
    public const int ZeroClearLength = 512;

    private readonly IGameMemory _mem;
    private readonly byte[] _buf = new byte[Lookback + ChunkSize + TrailSlack];

    // region snapshot reused each Tick to avoid one VirtualQueryEx walk per hot chunk
    private readonly List<(long rbase, long rsize)> _regionSnap = new();

    public ChunkReader(IGameMemory mem) => _mem = mem;

    /// <summary>The shared read buffer the Read methods fill; valid until the next read.</summary>
    public byte[] Buf => _buf;

    /// <summary>The regions captured by the last <see cref="Snapshot"/>.</summary>
    public IReadOnlyList<(long rbase, long rsize)> Regions => _regionSnap;

    /// <summary>Snapshot regions once and reuse them for both sweep phases.</summary>
    public void Snapshot()
    {
        _regionSnap.Clear();
        foreach (var r in _mem.Regions()) _regionSnap.Add(r);
    }

    /// <summary>Read a hot chunk with lookback using the per-Tick snapshot. Returns bytes read (0 on failure).</summary>
    public int Read(long chunkStart, out int lookback, out int searchable)
    {
        lookback = 0;
        searchable = 0;
        foreach (var (rbase, rsize) in _regionSnap)
        {
            if (chunkStart < rbase || chunkStart >= rbase + rsize) continue;
            return ReadInRegion(chunkStart, rbase, rbase + rsize, out lookback, out searchable);
        }
        return 0;
    }

    /// <summary>Read [chunkStart, L, chunkStart + ChunkSize + TrailSlack) clamped to the region, zero-clearing the tail
    /// past readLen. Returns bytes read into Buf (0 on failure).</summary>
    public int ReadInRegion(long chunkStart, long rbase, long rend,
                            out int lookback, out int searchable)
    {
        lookback = (int)Math.Min(Lookback, chunkStart - rbase);
        long readStart = chunkStart - lookback;
        long readEnd = Math.Min(rend, chunkStart + ChunkSize + TrailSlack);
        int readLen = (int)(readEnd - readStart);

        if (readLen <= 0 || readLen > _buf.Length) { lookback = 0; searchable = 0; return 0; }
        int got = _mem.ReadInto(readStart, _buf, readLen);
        if (got == 0) { lookback = 0; searchable = 0; return 0; }

        // Clear the tail so stale bytes from a longer previous read do not reach the scanner.
        if (readLen < _buf.Length)
            Array.Clear(_buf, readLen, Math.Min(ZeroClearLength, _buf.Length - readLen));

        searchable = (int)Math.Min(ChunkSize, rend - chunkStart);
        return got;
    }
}
