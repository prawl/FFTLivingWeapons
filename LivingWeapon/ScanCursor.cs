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
/// Pure region-ordering/cursor-resumption seam for the LW-27/LW-31 dev spikes' incremental
/// census walk (docs/TODO.md). A one-shot 512MB gulp per keypress burned its whole budget
/// scanning LOW addresses (this game's committed heap is enormous below the UI text arenas)
/// before ever reaching the HIGH addresses where the real on-screen strings live (owner's Cheat
/// Engine hits: 0x4D9BFBF253, 0x4EDBAB64B0, 0x4F5BFA5CF5), and the resulting multi-hundred-ms
/// synchronous block on the shared Engine tick thread was long enough to swallow a sibling
/// spike's F6 keypress edge entirely (see AttackCardSpike.cs's class doc for the full account).
/// <see cref="SortDescending"/> makes the walk hit the high addresses first; <see cref="NextSlice"/>
/// advances a bounded amount of work per call so a single Tick never blocks for long, and a
/// caller-held <see cref="RegionCursor"/> lets the walk resume exactly where it left off across
/// many Tick calls until the whole snapshot has been covered.
/// </summary>
internal static class ScanCursor
{
    /// <summary>Regions ordered by base address, highest first. Copies rather than mutating the
    /// input list (a ChunkReader snapshot may be read elsewhere).</summary>
    internal static List<(long rbase, long rsize)> SortDescending(IEnumerable<(long rbase, long rsize)> regions)
    {
        var sorted = new List<(long rbase, long rsize)>(regions);
        sorted.Sort((a, b) => b.rbase.CompareTo(a.rbase));
        return sorted;
    }

    /// <summary>Returns the next slice of chunk-aligned reads to perform (each a (regionBase,
    /// regionEnd, chunkStart) tuple, ready for ChunkReader.ReadInRegion), advancing cursor by
    /// roughly budgetBytes of region coverage. Always returns at least one chunk when any work
    /// remains, even if budgetBytes is smaller than one chunk, so the walk always makes forward
    /// progress. Returns an empty list once the cursor is (or becomes) done; a done cursor is
    /// never un-done by a later call.</summary>
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

        // Normalize past any region(s) the last chunk just exhausted so Done is accurate
        // immediately, rather than one call late (a caller checking cursor.Done right after this
        // call must see the true end of the walk, not "one more empty call away from it").
        SkipExhausted(regions, ref idx, ref addr);
        cursor = idx >= regions.Count ? new RegionCursor(idx, 0, true) : new RegionCursor(idx, addr, false);
        return slice;
    }

    /// <summary>Advances past any region(s) already fully covered by addr, landing idx/addr on
    /// the next region with work left (or idx == regions.Count when none remains).</summary>
    private static void SkipExhausted(IReadOnlyList<(long rbase, long rsize)> regions, ref int idx, ref long addr)
    {
        while (idx < regions.Count && addr >= regions[idx].rbase + regions[idx].rsize)
        {
            idx++;
            addr = idx < regions.Count ? regions[idx].rbase : 0;
        }
    }
}
