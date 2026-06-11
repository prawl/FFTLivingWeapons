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
    public const int TrailSlack = 64;

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
