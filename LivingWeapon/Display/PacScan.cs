using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// The needle search behind <see cref="ModdedPac"/>. Streams the 65 MB modded.pac in fixed
/// chunks through one window buffer, so peak footprint is one chunk and the core is testable
/// against a <see cref="MemoryStream"/>. The LOH threshold is 85,000 bytes and the default chunk
/// clears it twelve times over, so chunking does not keep allocations off the LOH; what matters
/// is reusing the window. A fresh array per chunk once produced about 69 MB of LOH garbage for a
/// single icon patch, and LOH collections stop every managed thread, Engine's tick loop included.
///
/// A needle straddling two chunks is invisible to either read alone, so <see cref="FindBatch"/>
/// carries the tail of each window forward, sized to the largest active needle, and skips hits
/// that end inside the carried bytes so nothing is counted twice. PacScanTests pins the straddle case.
/// </summary>
internal static class PacScan
{
    /// <summary>No occurrence found anywhere in the stream.</summary>
    internal const long NotFound = -1;

    /// <summary>More than one occurrence found; the caller must not guess which one is real.</summary>
    internal const long Ambiguous = -2;

    /// <summary>Scans from the stream's current position to its end for one occurrence of
    /// <paramref name="needle"/> and returns its absolute offset. <see cref="NotFound"/> for a null
    /// or empty needle, a needle longer than what remains, or no match; <see cref="Ambiguous"/> the
    /// moment a second occurrence appears. The caller owns positioning.</summary>
    internal static long FindSingle(Stream stream, byte[] needle, int chunkSize = 1 << 20)
    {
        if (needle == null || needle.Length == 0) return NotFound;
        return FindBatch(stream, new[] { needle }, chunkSize)[0];
    }

    /// <summary>Finds one occurrence of each needle in a single pass. Looping
    /// <see cref="FindSingle"/> re-reads the stream per needle, about 15.7 GB for 242 icons over a
    /// 65 MB pac. Same result convention as FindSingle, one per needle in order. A null, empty or
    /// too-long needle never enters the scan. I/O exceptions are the caller's, ModdedPac wraps
    /// every I/O path.</summary>
    internal static IReadOnlyList<long> FindBatch(Stream stream, IReadOnlyList<byte[]> needles, int chunkSize = 1 << 20)
    {
        var results = new long[needles.Count];
        long streamStart = stream.Position;
        long remaining = stream.Length - streamStart;

        // Only needles that could appear enter the scan; the rest are NotFound for free.
        // maxNeedleLen sizes the carried overlap.
        var active = new List<(int ResultIndex, byte[] Needle)>();
        int maxNeedleLen = 0;
        for (int i = 0; i < needles.Count; i++)
        {
            var needle = needles[i];
            results[i] = NotFound;
            if (needle == null || needle.Length == 0 || needle.Length > remaining) continue;
            active.Add((i, needle));
            if (needle.Length > maxNeedleLen) maxNeedleLen = needle.Length;
        }
        if (active.Count == 0) return results;

        if (chunkSize < maxNeedleLen) chunkSize = maxNeedleLen;
        int overlapLen = maxNeedleLen - 1;

        // One window for the whole scan, a chunk plus the largest overlap, refilled in place.
        byte[] window = new byte[chunkSize + overlapLen];
        long windowAbsoluteStart = streamStart;
        int windowLen = 0;
        bool haveCarry = false;
        var resolved = new bool[needles.Count];   // per original index, true once Ambiguous

        while (true)
        {
            int carry = haveCarry ? overlapLen : 0;
            if (haveCarry)
                // Array.Copy is overlap-safe where Buffer.BlockCopy makes no promise, and the
                // two ranges overlap whenever a test picks a chunk smaller than the overlap.
                Array.Copy(window, windowLen - overlapLen, window, 0, overlapLen);

            int filled = ReadFully(stream, window, carry, chunkSize);
            windowLen = carry + filled;
            if (filled == 0) break;   // nothing new, the last window covered everything

            // Positions before freshDataStart were searched by an earlier window; only hits
            // reaching past it are new. Without this a short needle sitting inside the carried
            // bytes would count twice.
            long freshDataStart = windowAbsoluteStart + carry;
            SearchWindow(window, windowLen, windowAbsoluteStart, freshDataStart, active, results, resolved);

            bool exhausted = filled < chunkSize;   // a short read from ReadFully means true EOF
            if (exhausted) break;

            windowAbsoluteStart += windowLen - overlapLen;
            haveCarry = overlapLen > 0;
        }

        return results;
    }

    /// <summary>Searches every active needle not yet Ambiguous against the window with the
    /// vectorized span IndexOf. A hit that ends at or before <paramref name="freshDataStart"/> was
    /// counted by an earlier window and is skipped.</summary>
    private static void SearchWindow(byte[] window, int windowLen, long windowAbsoluteStart, long freshDataStart,
        List<(int ResultIndex, byte[] Needle)> active, long[] results, bool[] resolved)
    {
        var span = new ReadOnlySpan<byte>(window, 0, windowLen);
        foreach (var (resultIndex, needle) in active)
        {
            if (resolved[resultIndex]) continue;

            int searchFrom = 0;
            while (searchFrom <= windowLen - needle.Length)
            {
                int local = span.Slice(searchFrom).IndexOf(needle);
                if (local < 0) break;
                int hit = searchFrom + local;
                searchFrom = hit + 1;

                long absoluteHit = windowAbsoluteStart + hit;
                if (absoluteHit + needle.Length <= freshDataStart) continue;   // fully old, already counted

                if (results[resultIndex] != NotFound)
                {
                    results[resultIndex] = Ambiguous;
                    resolved[resultIndex] = true;
                    break;
                }
                results[resultIndex] = absoluteHit;
            }
        }
    }

    /// <summary>Loops <see cref="Stream.Read"/> until the request is filled or the stream ends.
    /// Read may return short before EOF, and treating that as EOF would truncate the scan and
    /// could report a second occurrence as a confident single hit. Only a non-positive return
    /// means EOF, so the result is short only there.</summary>
    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }
}
