using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// The pure needle-search core behind <see cref="ModdedPac"/>.
/// Streams a haystack (modded.pac is roughly 65 MB) in fixed-size chunks instead of loading it
/// whole, and reuses one window buffer across the whole scan rather than allocating a fresh one
/// per chunk.
///
/// WHAT CHUNKING ACTUALLY BUYS -- corrected here after the round-1 comment claimed it "keeps the
/// subsystem off the Large Object Heap entirely", which is arithmetically false. The LOH
/// threshold is 85,000 bytes, and the default chunk size below (1,048,576 bytes) clears that
/// twelve times over on its own, so chunking buys nothing there by itself. The real benefits are
/// (a) a BOUNDED peak footprint -- one chunk-sized window live at a time instead of the whole
/// ~65 MB file -- and (b) a streamable core that is unit-testable against a plain
/// <see cref="MemoryStream"/> with no file system involved. Getting (a) right requires reusing
/// the window buffer across iterations. The round-1 shape allocated a fresh window array per
/// chunk, which for a single-icon patch produced roughly 69 MB of LOH garbage -- MORE than the
/// 65 MB read it existed to avoid -- and LOH collections are gen2 and STOP every MANAGED THREAD,
/// Engine's 33 ms tick loop included. This class allocates its window buffer once per call and
/// refills it in place every iteration.
///
/// The one subtlety a naive chunked scan gets wrong. A needle that straddles the boundary
/// between two chunks is invisible to either chunk read alone. <see cref="FindBatch"/> carries
/// the trailing bytes of each window forward as a prefix on the next read (sized to the LARGEST
/// active needle, so a batch of mixed needle lengths is handled correctly too -- see its own
/// remarks for why a naive "needle length, 1" carry is not safe once more than one needle length
/// is in play), and de-duplicates re-examined bytes by their absolute stream position rather than
/// by re-deriving overlap-region math per needle. Deleting or shrinking that carry is the
/// specific bug this class exists to make impossible -- see PacScanTests for the pinned case.
/// </summary>
internal static class PacScan
{
    /// <summary>No occurrence found anywhere in the stream.</summary>
    internal const long NotFound = -1;

    /// <summary>More than one occurrence found; the caller must not guess which one is real.</summary>
    internal const long Ambiguous = -2;

    /// <summary>
    /// Scans <paramref name="stream"/> from its current position to its end for a single
    /// occurrence of <paramref name="needle"/>, returning its absolute stream offset. A thin
    /// wrapper over <see cref="FindBatch"/> with one needle. Returns <see cref="NotFound"/> for a
    /// null/empty needle, a needle longer than the remaining stream, or no match;
    /// <see cref="Ambiguous"/> the instant a second occurrence is seen. Never seeks the stream to
    /// 0 itself; the caller owns positioning.
    /// </summary>
    internal static long FindSingle(Stream stream, byte[] needle, int chunkSize = 1 << 20)
    {
        if (needle == null || needle.Length == 0) return NotFound;
        return FindBatch(stream, new[] { needle }, chunkSize)[0];
    }

    /// <summary>
    /// Scans <paramref name="stream"/> from its current position to its end for a single
    /// occurrence of EACH needle in <paramref name="needles"/>, in one sequential pass -- the
    /// R2-2 fix. A per-needle loop over <see cref="FindSingle"/> re-reads the whole stream once
    /// per needle (242 managed icons over a 65 MB pac is ~15.7 GB of reads for one full re-tier),
    /// where this walks the stream once regardless of needle count. Returns one result per
    /// needle, same order and same NotFound/Ambiguous/offset convention as <see cref="FindSingle"/>.
    /// A null/empty needle, or one longer than the remaining stream, resolves to
    /// <see cref="NotFound"/> without ever entering the scan. Never catches I/O exceptions --
    /// that is the caller's concern (<see cref="ModdedPac"/> wraps every I/O path).
    /// </summary>
    internal static IReadOnlyList<long> FindBatch(Stream stream, IReadOnlyList<byte[]> needles, int chunkSize = 1 << 20)
    {
        var results = new long[needles.Count];
        long streamStart = stream.Position;
        long remaining = stream.Length - streamStart;

        // Only needles that could possibly appear ever enter the scan; everything else resolves
        // to NotFound for free. `maxNeedleLen` sizes the carried overlap (see the class remarks).
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

        // one reusable window buffer for the whole scan (R2-1): sized for one full chunk plus the
        // largest needle's overlap, allocated once and refilled in place every iteration.
        byte[] window = new byte[chunkSize + overlapLen];
        long windowAbsoluteStart = streamStart;
        int windowLen = 0;
        bool haveCarry = false;
        var resolved = new bool[needles.Count];   // per ORIGINAL index; true once Ambiguous (stop re-checking)

        while (true)
        {
            int carry = haveCarry ? overlapLen : 0;
            if (haveCarry)
                // Array.Copy is explicitly overlap-safe (unlike Buffer.BlockCopy, whose docs make
                // no such guarantee) -- source and destination can be the same array here when
                // windowLen, overlapLen < overlapLen (a small chunkSize in a test).
                Array.Copy(window, windowLen - overlapLen, window, 0, overlapLen);

            int filled = ReadFully(stream, window, carry, chunkSize);
            windowLen = carry + filled;
            if (filled == 0) break;   // nothing new -- the previous iteration already covered everything

            // Every position before `freshDataStart` was already fully covered (and searched) by
            // an earlier window; only hits that reach at or past it are newly discoverable this
            // pass. This is what makes the mixed-needle-length carry safe. A short needle fully
            // contained in the old, re-copied-down portion of `window` would otherwise be found
            // (and counted) a second time here.
            long freshDataStart = windowAbsoluteStart + carry;
            SearchWindow(window, windowLen, windowAbsoluteStart, freshDataStart, active, results, resolved);

            bool exhausted = filled < chunkSize;   // a short/partial read here means true EOF
            if (exhausted) break;

            windowAbsoluteStart += windowLen - overlapLen;
            haveCarry = overlapLen > 0;
        }

        return results;
    }

    /// <summary>Searches every still-active, not-yet-Ambiguous needle against the current window,
    /// using <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T},ReadOnlySpan{T})"/> (SIMD-
    /// vectorized in .NET 8 -- materially faster than a hand-rolled first-byte prefilter over a
    /// 65 MB pac). A hit whose END does not reach past <paramref name="freshDataStart"/> was
    /// already found (and counted, or rejected as a second occurrence) in an earlier window, so
    /// it is skipped rather than re-counted; see the class remarks for why this is the correct
    /// general rule once needle lengths differ within one batch.</summary>
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

    /// <summary>Fills <paramref name="buffer"/>[<paramref name="offset"/>..+<paramref name="count"/>)
    /// by looping <see cref="Stream.Read"/> until either the request is fully satisfied or the
    /// stream truly ends. R2-5 <c>Stream.Read</c> may legally return fewer bytes than requested
    /// before EOF -- treating any short read as EOF would truncate the scan (a false NotFound at
    /// best; at worst a second occurrence past the truncation point is never seen, so a genuinely
    /// Ambiguous case is reported as a confident single hit and a write proceeds on the strength
    /// of a broken uniqueness guarantee). A non-positive <c>Read</c> return is the only valid EOF
    /// signal. Returns the number of bytes actually filled, less than <paramref name="count"/>
    /// only at true EOF.</summary>
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
