using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Chunked reads for the display sweep: one shared buffer (lookback prefix + chunk +
/// trailing slack) filled from a per-Tick region snapshot. Both sweep phases read through
/// here -- the hot phase by bare chunk address (resolved against the snapshot), the
/// background walk with its region bounds already in hand. Snapshot() is taken once per
/// Tick so a hot pass never costs one VirtualQueryEx walk per chunk.
/// </summary>
internal sealed class ChunkReader
{
    public const int ChunkSize  = 4 * 1024 * 1024;
    public const int Lookback   = 4096;

    /// <summary>Trailing slack past a chunk's own searchable window, for anchors/slots that
    /// straddle a chunk boundary FORWARD (CardScanner's bidirectional attribution can now find a
    /// weapon's flavor AFTER its "Kills: " hit: the Reliquary Phase-2 equip-meter layout). Real
    /// max living flavor is 152 chars / 304 UTF-16 bytes (Excalibur id35); a hit at the very end
    /// of the searchable window can need the owner's flavor up to ~344 bytes past chunk end
    /// (anchor + "Kills: " + the meter slot + the "\n\n" gaps). 4096 (== Lookback) covers that
    /// with wide margin at a cost of ~4KB on a 4MB buffer. Chunk boundaries are DETERMINISTIC
    /// per region, so an undersized slack drops that card's site EVERY pass (no self-heal);
    /// this must be sized, not guessed. See CardPatterns.FitsTrailSlack for the checked bound and
    /// Display's ctor for where it's exercised at startup.</summary>
    public const int TrailSlack = 4096;

    private readonly IGameMemory _mem;
    private readonly byte[] _buf = new byte[Lookback + ChunkSize + TrailSlack];

    // region snapshot reused each Tick to avoid one VirtualQueryEx walk per hot chunk
    private readonly List<(long rbase, long rsize)> _regionSnap = new();

    public ChunkReader(IGameMemory mem) => _mem = mem;

    /// <summary>The shared read buffer the Read methods fill; valid until the next read.</summary>
    public byte[] Buf => _buf;

    /// <summary>The regions captured by the last <see cref="Snapshot"/>.</summary>
    public IReadOnlyList<(long rbase, long rsize)> Regions => _regionSnap;

    /// <summary>Snapshot regions once -- reused for both sweep phases to avoid repeated
    /// VirtualQueryEx walks.</summary>
    public void Snapshot()
    {
        _regionSnap.Clear();
        foreach (var r in _mem.Regions()) _regionSnap.Add(r);
    }

    /// <summary>Read a hot chunk with correct lookback, using the per-Tick region snapshot.
    /// Returns bytes read (0 on failure).</summary>
    public int Read(long chunkStart, out int lookback, out int searchable)
    {
        lookback   = 0;
        searchable = 0;
        foreach (var (rbase, rsize) in _regionSnap)
        {
            if (chunkStart < rbase || chunkStart >= rbase + rsize) continue;
            return ReadInRegion(chunkStart, rbase, rbase + rsize, out lookback, out searchable);
        }
        return 0;
    }

    /// <summary>Read [chunkStart - L, chunkStart + ChunkSize + TrailSlack) clamped to the region.
    /// Clears the buffer tail past readLen to prevent stale bytes from a prior longer read from
    /// leaking into the scanner (region-end chunks are shorter than a full chunk).
    /// Returns bytes read into Buf (0 on failure).</summary>
    public int ReadInRegion(long chunkStart, long rbase, long rend,
                            out int lookback, out int searchable)
    {
        lookback   = (int)Math.Min(Lookback, chunkStart - rbase);
        long readStart = chunkStart - lookback;
        long readEnd   = Math.Min(rend, chunkStart + ChunkSize + TrailSlack);
        int readLen    = (int)(readEnd - readStart);

        if (readLen <= 0 || readLen > _buf.Length) { lookback = 0; searchable = 0; return 0; }
        int got = _mem.ReadInto(readStart, _buf, readLen);
        if (got == 0) { lookback = 0; searchable = 0; return 0; }

        // Clear tail so stale bytes from a previous longer read don't leak into the scanner.
        if (readLen < _buf.Length)
            Array.Clear(_buf, readLen, Math.Min(512, _buf.Length - readLen));

        searchable = (int)Math.Min(ChunkSize, rend - chunkStart);
        return got;
    }
}
