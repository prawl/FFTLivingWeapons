using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Cursor state for an incremental, high-address-first region walk: which region the walk is
/// on and the next chunk-aligned address within it, or done. <see cref="AtStart"/> seeds a fresh
/// walk over an already-sorted region list (an empty list starts done).
/// </summary>
internal readonly record struct RegionCursor(int RegionIndex, long NextAddr, bool Done)
{
    internal static RegionCursor AtStart(IReadOnlyList<(long rbase, long rsize)> regions)
        => regions.Count == 0 ? new RegionCursor(0, 0, true) : new RegionCursor(0, regions[0].rbase, false);
}

/// <summary>
/// Region ordering and cursor resumption for the LW-27/LW-31 dev spikes' incremental census walk (docs/TODO.md).
/// A one-shot 512MB scan spent its budget on low addresses and blocked the shared Engine tick long enough to
/// swallow a sibling spike's F6 keypress (see AttackCardSpike.cs). The live strings sit high (Cheat Engine hits
/// 0x4D9BFBF253, 0x4EDBAB64B0, 0x4F5BFA5CF5), so SortDescending walks high first and NextSlice bounds the work per call.
/// </summary>
internal static class ScanCursor
{
    /// <summary>Regions ordered by base address, highest first. Returns a copy.</summary>
    internal static List<(long rbase, long rsize)> SortDescending(IEnumerable<(long rbase, long rsize)> regions)
    {
        var sorted = new List<(long rbase, long rsize)>(regions);
        sorted.Sort((a, b) => b.rbase.CompareTo(a.rbase));
        return sorted;
    }

    /// <summary>Next slice of chunk-aligned (regionBase, regionEnd, chunkStart) reads for ChunkReader.ReadInRegion,
    /// advancing cursor by about budgetBytes. Returns at least one chunk while work remains, empty once done.</summary>
    internal static List<(long rbase, long rend, long chunkStart)> NextSlice(
        IReadOnlyList<(long rbase, long rsize)> regions, ref RegionCursor cursor, long budgetBytes)
    {
        var slice = new List<(long rbase, long rend, long chunkStart)>();
        if (cursor.Done) return slice;

        int idx = cursor.RegionIndex;
        long addr = cursor.NextAddr;
        long used = 0;

        while (used < budgetBytes)
        {
            SkipExhausted(regions, ref idx, ref addr);
            if (idx >= regions.Count) break;

            var (rbase, rsize) = regions[idx];
            long rend = rbase + rsize;
            long thisLen = Math.Min(ChunkReader.ChunkSize, rend - addr);
            slice.Add((rbase, rend, addr));
            used += thisLen;
            addr += ChunkReader.ChunkSize;
        }

        // Skip regions the last chunk exhausted so Done is accurate right away.
        SkipExhausted(regions, ref idx, ref addr);
        cursor = idx >= regions.Count ? new RegionCursor(idx, 0, true) : new RegionCursor(idx, addr, false);
        return slice;
    }

    /// <summary>Advances past fully covered regions to the next with work left (idx == regions.Count when none).</summary>
    private static void SkipExhausted(IReadOnlyList<(long rbase, long rsize)> regions, ref int idx, ref long addr)
    {
        while (idx < regions.Count && addr >= regions[idx].rbase + regions[idx].rsize)
        {
            idx++;
            addr = idx < regions.Count ? regions[idx].rbase : 0;
        }
    }
}
