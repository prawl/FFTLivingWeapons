using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// A4-7 (round 3): split out of IconCacheHide.Policy.cs once the A4-2/A4-3/A4-6 additions pushed
/// that file past the 200-line trigger. The real seam: Policy.cs decides WHAT a byte pattern means
/// (classification, outcomes, log lines); this file is purely the raw per-chunk byte-buffer walk
/// that finds candidate strings and pointers in the first place. Still pure -- no
/// <see cref="IGameMemory"/> anywhere, only the chunk buffer IconCacheHide.cs's <c>ForEachChunk</c>
/// hands in.
/// </summary>
internal static partial class IconCacheHidePolicy
{
    /// <summary>PASS 1 for one chunk buffer: a hit counts as a string start only when preceded by
    /// NUL (or sitting at a region's byte 0 with no lookback -- P7/R5), forward-reads up to
    /// <see cref="CStringMax"/> bytes from the SAME buffer and keeps only an EXACT needle match
    /// (R6). Pure: only <paramref name="buf"/> and the ints/lookup are read.
    /// A3-2 (P9 restated): each prefix searches out to <c>min(buf.Length, end + prefix.Length -
    /// 1)</c>, not just <c>end = lookback + searchable</c> -- a prefix starting in that last
    /// stretch still sits whole in the trail slack; a hit found from either side of the edge
    /// lands on the same address and is recorded once. This span, not
    /// <see cref="ChunkReader.Lookback"/> (never searched by pass 1), is what covers a boundary.
    /// A4-6 (N1): the furthest this can reach past a chunk's own searchable window is
    /// <c>PrefixMax - 1</c> (the span extension) plus <see cref="CStringMax"/> (the forward read)
    /// -- <see cref="ChunkReaderZeroClearLength"/> documents that ChunkReader.cs zero-clears at
    /// least that many bytes past a short region-end read, so this never reads a stale byte from a
    /// previous longer read out there; it reads a deterministic zero instead.</summary>
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

    /// <summary>PASS 2 for one chunk buffer: every 8-byte-ALIGNED (absolute address) u64 equal to
    /// a PASS-1 string address is a candidate record holder.</summary>
    internal static void ScanChunkForPointers(byte[] buf, int lookback, int searchable, long bufBase,
        IReadOnlyDictionary<long, (int Id, string Surface, NeedleKind Kind)> stringMap,
        List<(long HolderAddr, long StringAddr, int Id, string Surface, NeedleKind Kind)> holders)
    {
        int end = lookback + searchable;
        int start = lookback;
        // P12 (A3-3): every holder the probes found was 8-byte aligned (tools/probes/
        // lw381_parts_cache_probe.py filters (p & 7) == 0) -- both records are heap objects with
        // a pointer at +0, which the allocator aligns. An unaligned holder is simply missed.
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
