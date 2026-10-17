using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// A4-7 (round 3), split out of IconCacheHide.Policy.cs past the 200-line trigger. Policy.cs decides
/// what a byte pattern means; this file walks the raw per-chunk buffer for candidate strings and
/// pointers. Pure, no <see cref="IGameMemory"/>.
/// </summary>
internal static partial class IconCacheHidePolicy
{
    /// <summary>Pass 1 finds needle strings in one chunk. A hit must follow a NUL (or sit at byte 0 of
    /// a region with no lookback, P7/R5) and match a needle in full over up to <see cref="CStringMax"/>
    /// bytes (R6). A3-2 (P9) each prefix searches to <c>min(buf.Length, end + prefix.Length, 1)</c>
    /// so a prefix straddling the end is found once, which <see cref="ChunkReader.Lookback"/> does not
    /// cover. A4-6 (N1) reads reach at most <c>PrefixMax, 1</c> + <see cref="CStringMax"/> past the
    /// window, and <see cref="ChunkReaderZeroClearLength"/> notes ChunkReader.cs zero-clears that much.</summary>
    internal static void ScanChunkForStrings(byte[] buf, int lookback, int searchable, long bufBase,
        IReadOnlyDictionary<string, (int Id, string Surface, NeedleKind Kind)> lookup,
        Dictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap)
    {
        int end = lookback + searchable;
        foreach (var prefix in Prefixes)
        {
            int scanEnd = Math.Min(buf.Length, end + prefix.Length - 1);
            int from = lookback;
            while (from < scanEnd)
            {
                int idx = IndexOf(buf, from, scanEnd, prefix);
                if (idx < 0) break;
                from = idx + 1;
                if (!((idx == 0 && lookback == 0) || buf[idx - 1] == 0)) continue;

                int windowLen = Math.Min(CStringMax, buf.Length - idx);
                var window = new byte[windowLen];
                Array.Copy(buf, idx, window, 0, windowLen);
                var match = IsExpectedString(window, lookup);
                if (match != null) stringMap[bufBase + idx] = match.Value;
            }
        }
    }

    /// <summary>Pass 2 treats any 8-aligned (absolute address) u64 equal to a pass-1 string address
    /// as a candidate record holder.</summary>
    internal static void ScanChunkForPointers(byte[] buf, int lookback, int searchable, long bufBase,
        IReadOnlyDictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap,
        List<(long HolderAddr, long StringAddr, int Id, string Surface, NeedleKind Kind)> holders)
    {
        int end = lookback + searchable;
        int start = lookback;
        // P12 (A3-3) every holder the probes found was 8-byte aligned (tools/probes/lw381_parts_cache_probe.py
        // filters (p & 7) == 0); both records are heap objects with a pointer at +0.
        while (((bufBase + start) & 7) != 0) start++;
        for (int i = start; i < end && i + 8 <= buf.Length; i += 8)
        {
            ulong ptr = BitConverter.ToUInt64(buf, i);
            if (ptr != 0 && stringMap.TryGetValue((long)ptr, out var match))
                holders.Add((bufBase + i, (long)ptr, match.Id, match.Surface, match.Kind));
        }
    }

    private static int IndexOf(byte[] buf, int from, int end, byte[] needle)
    {
        int hit = new ReadOnlySpan<byte>(buf, from, end - from).IndexOf(needle);
        return hit < 0 ? -1 : from + hit;
    }
}
