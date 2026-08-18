using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Locates (and caches) the writable UE string pool region(s) the equip card re-materializes
/// its description from (LW-37). Mirrors GrowthEngine.Locate's cache-or-rescan template: a
/// cached set is re-verified in place before it is trusted; a miss (or no cache yet) rescans
/// every region IGameMemory.Regions() yields, keeping each that PoolLocatorPolicy scores as pool.
///
/// Why ALL of them, not the single best (live finding 2026-07-08): the process holds SEVERAL
/// name-bearing baked copies of the item descriptions (a low-address working table plus the
/// high-address table the card actually materializes from), each attributing all 121 living
/// weapons. There is no static byte signature for WHICH one the card reads, so picking the
/// "most distinct / first" one painted the wrong copy and left the card baked. PoolPaint instead
/// paints EVERY qualifying region: they are few and small, and every write is name-gated,
/// foreign-refused, and Writable-checked, so painting a non-source baked copy is harmless.
///
/// Correctness invariant (build-plan B1): Regions() yields ONLY committed/PRIVATE/writable
/// memory (Mem.cs), so any region located is writable BY CONSTRUCTION: a read-only pool is never
/// in Regions() at all, so LocateAll simply omits it (caller keeps sweeping) rather than
/// reporting a write target that cannot be painted.
///
/// PREMISE STATUS: that the specific named pool this class locates is the copy the card actually
/// materializes from is docs/LIVE_LEDGER.md's [card-materializes-from-named-pool] row -- filed
/// Uncertain (commit/changelog grade, one below ledger-PROVEN), owner-only flip. LW-257 commit 1
/// does not touch this premise at all; it matters only for a targeted per-region drain check that
/// is explicitly OUT of commit 1's scope.
/// </summary>
internal sealed class PoolLocator
{
    private readonly IGameMemory _mem;
    private readonly CardPatterns _pats;
    private readonly ChunkReader _reader;

    private readonly List<(long baseAddr, long size)> _cached = new();

    // LW-257 (item 7): Signatures.StuckEdge state for the locate log line below -- a persistent
    // "found nothing" state (or a pool that never resolves) would otherwise log every single
    // Tick forever once promoted out of #if LWDEV. Mirrors Display.PoolPaint.cs's own
    // _noPoolRegionLatched, kept separate since the two fire from different call sites.
    private bool _noneFoundLatched;

    public PoolLocator(IGameMemory mem, CardPatterns pats)
    {
        _mem = mem;
        _pats = pats;
        _reader = new ChunkReader(mem);
    }

    /// <summary>Every writable region whose buffer holds baked pool entries (a "Kills: " hit with
    /// its owner weapon's name adjacent). Cached; a cache whose every region still reads as pool is
    /// reused without a rescan. Empty when nothing qualifies (caller keeps sweeping).</summary>
    internal IReadOnlyList<(long baseAddr, long size)> LocateAll()
    {
        if (_cached.Count > 0 && AllCachedStillPool()) return _cached;

        _cached.Clear();
        foreach (var (rbase, rsize) in _mem.Regions())
            if (ScanRegion(rbase, rsize).isPool) _cached.Add((rbase, rsize));
        // LW-257: promoted from #if LWDEV to unconditional Debug (file-only, no console noise --
        // ModLogger.Debug always writes livingweapon.log, LogLevel only gates the console mirror)
        // so a RELEASE tape can answer "how many pool regions, and how big" without a dev rebuild.
        // Size is new alongside the base address: the base-only line could not tell a small region
        // from one large enough to make MaybePoolPaint's per-region scan cost (Display.PoolPaint.
        // cs's own ScanPoolRegion doc) actually matter. Rate-limited (item 7) when nothing is
        // found: StuckEdge logs that transition once instead of every Tick a drought persists;
        // a found-something result (rare once coverage latches -- LocateAll then short-circuits
        // above) always logs, since it is never a per-tick event in practice.
        bool noneFound = _cached.Count == 0;
        bool edge = Signatures.StuckEdge(ref _noneFoundLatched, noneFound);
        if (!noneFound || edge)
        {
            var parts = _cached.ConvertAll(r => "0x" + r.baseAddr.ToString("X") + ":" + r.size);
            ModLogger.Debug(LogVerb.Display, $"LW37 locate: {_cached.Count} named-pool region(s) at [{string.Join(", ", parts)}]");
        }
        return _cached;
    }

    /// <summary>Re-verify each cached region still reads as pool (relocation / realloc guard).</summary>
    private bool AllCachedStillPool()
    {
        foreach (var r in _cached) if (!ScanRegion(r.baseAddr, r.size).isPool) return false;
        return true;
    }

    /// <summary>Drop the cache so the next LocateAll rescans from scratch (regions may have
    /// relocated, or their buffers been reallocated: called from Display.Invalidate()).</summary>
    internal void Invalidate() => _cached.Clear();

    /// <summary>Test-only cache seed (mirrors GrowthEngine.SeedStructForSlotForTest): drives
    /// LocateAll's revalidate path directly against pinned regions, without needing a prior scan.</summary>
    internal void SeedForTest(params (long baseAddr, long size)[] regions)
    {
        _cached.Clear();
        _cached.AddRange(regions);
    }

    /// <summary>Aggregate PoolLocatorPolicy.Scan across every chunk of [rbase, rbase+rsize)
    /// via ChunkReader (not a slack-less whole-region read: a real pool can exceed
    /// ChunkSize). IsPool and the distinct-weapon count union across chunks.</summary>
    private (bool isPool, int distinct) ScanRegion(long rbase, long rsize)
    {
        var ids = new HashSet<int>();
        long rend = rbase + rsize;
        long chunkStart = rbase;
        while (chunkStart < rend)
        {
            int read = _reader.ReadInRegion(chunkStart, rbase, rend, out int lookback, out int searchable);
            if (read == 0) break;
            var scan = PoolLocatorPolicy.Scan(_reader.Buf, lookback, searchable, _pats);
            foreach (var h in scan.Hits) ids.Add(h.Id);
            chunkStart += ChunkReader.ChunkSize;
        }
        return (ids.Count > 0, ids.Count);
    }
}
