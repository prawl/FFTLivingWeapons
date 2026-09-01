using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-381: the pure, testable core of the modded.pac patcher. No file system involved -- every
/// case here drives <see cref="PacScan.FindSingle"/> against a <see cref="MemoryStream"/>, so the
/// chunk-boundary behavior (the whole reason this class exists instead of one
/// <c>File.ReadAllBytes(...).IndexOf(...)</c> call) is exercised directly and cheaply.
/// </summary>
public class PacScanTests
{
    private static byte[] Filler(int len, byte value = 0xAA)
    {
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = value;
        return b;
    }

    // ---- LOAD-BEARING: a needle that straddles a chunk boundary must still be found. This is
    // the one test that pins the carried-overlap logic: deleting the overlap (or getting its
    // size wrong) makes the needle invisible to any single chunk read and this test goes red. ----

    [Fact]
    public void FindSingle_NeedleStraddlingChunkBoundary_IsFound()
    {
        const int chunkSize = 64;
        byte[] needle = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        // Needle begins 3 bytes before the chunk boundary and ends 5 bytes after it, so no
        // single 64-byte read (without the needle.Length-1 overlap) ever contains it whole.
        int needleStart = chunkSize - 3;
        var haystack = Filler(200);
        Array.Copy(needle, 0, haystack, needleStart, needle.Length);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize);

        Assert.Equal(needleStart, offset);
    }

    [Fact]
    public void FindSingle_TwoOccurrences_ReturnsAmbiguous()
    {
        const int chunkSize = 64;
        byte[] needle = { 0x11, 0x22, 0x33, 0x44 };
        var haystack = Filler(200);
        Array.Copy(needle, 0, haystack, 10, needle.Length);
        Array.Copy(needle, 0, haystack, 150, needle.Length);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize);

        Assert.Equal(PacScan.Ambiguous, offset);
    }

    [Fact]
    public void FindSingle_NeedleAtOffsetZero_IsFound()
    {
        byte[] needle = { 0x01, 0x02, 0x03 };
        var haystack = Filler(50);
        Array.Copy(needle, 0, haystack, 0, needle.Length);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize: 16);

        Assert.Equal(0, offset);
    }

    [Fact]
    public void FindSingle_NeedleAtVeryEndOfStream_IsFound()
    {
        byte[] needle = { 0x09, 0x08, 0x07 };
        var haystack = Filler(50);
        Array.Copy(needle, 0, haystack, haystack.Length - needle.Length, needle.Length);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize: 16);

        Assert.Equal(haystack.Length - needle.Length, offset);
    }

    [Fact]
    public void FindSingle_NeedleAbsent_ReturnsNotFound()
    {
        byte[] needle = { 0xFF, 0xFE, 0xFD };
        var haystack = Filler(50);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize: 16);

        Assert.Equal(PacScan.NotFound, offset);
    }

    [Fact]
    public void FindSingle_NeedleLongerThanStream_ReturnsNotFound()
    {
        byte[] needle = Filler(100);
        var haystack = Filler(10);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize: 16);

        Assert.Equal(PacScan.NotFound, offset);
    }

    [Fact]
    public void FindSingle_EmptyNeedle_ReturnsNotFound()
    {
        using var stream = new MemoryStream(Filler(50));
        Assert.Equal(PacScan.NotFound, PacScan.FindSingle(stream, Array.Empty<byte>()));
    }

    [Fact]
    public void FindSingle_NullNeedle_ReturnsNotFound()
    {
        using var stream = new MemoryStream(Filler(50));
        Assert.Equal(PacScan.NotFound, PacScan.FindSingle(stream, null!));
    }

    // ---- chunkSize smaller than the needle must be clamped internally rather than trusted,
    // per spec ("chunkSize must exceed needle.Length; clamp it up internally"). A chunkSize of 1
    // against an 8-byte needle would never find anything if the clamp were missing. ----

    [Fact]
    public void FindSingle_ChunkSizeSmallerThanNeedle_IsClampedAndStillFinds()
    {
        byte[] needle = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        var haystack = Filler(100);
        Array.Copy(needle, 0, haystack, 40, needle.Length);

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize: 1);

        Assert.Equal(40, offset);
    }

    // ---- reads from the stream's current position; does not seek to 0 itself ----

    [Fact]
    public void FindSingle_ReadsFromCurrentPosition_DoesNotSeekToZero()
    {
        byte[] needle = { 0xAB, 0xCD };
        var haystack = Filler(50);
        Array.Copy(needle, 0, haystack, 5, needle.Length);   // absolute offset 5
        Array.Copy(needle, 0, haystack, 30, needle.Length);  // a second copy before offset 5's data if we started at 0

        using var stream = new MemoryStream(haystack);
        stream.Position = 20;   // skip past the first occurrence

        long offset = PacScan.FindSingle(stream, needle, chunkSize: 16);

        // Only the copy at absolute 30 is visible from position 20 onward -- a single hit.
        Assert.Equal(30, offset);
    }

    // ---- R2-5 fix round: two latent bugs in the "pure testable core". Both are unreachable with
    // production needles (12,384 / 51,296 bytes) but this class is billed as the pure core and
    // must be correct at its own boundaries. ----

    // R2-5, bug 1: with needle.Length == 1 the per-needle overlap is 0 bytes, so a naive
    // implementation that only advances its absolute-start bookkeeping via the overlap carry
    // never advances it at all -- every hit in the second or later chunk gets reported short by
    // a whole number of chunks. Place a unique single byte value well into the SECOND chunk and
    // assert its true absolute offset comes back, not an offset pinned to (or near) the stream
    // start.
    [Fact]
    public void FindSingle_OneByteNeedle_FoundInSecondChunk_ReportsCorrectAbsoluteOffset()
    {
        const int chunkSize = 32;
        const int trueOffset = 50;   // well inside the second 32-byte chunk (32..63)
        byte[] needle = { 0x5A };
        var haystack = Filler(100, value: 0x00);
        haystack[trueOffset] = 0x5A;

        using var stream = new MemoryStream(haystack);
        long offset = PacScan.FindSingle(stream, needle, chunkSize);

        Assert.Equal(trueOffset, offset);
    }

    // R2-5, bug 2: Stream.Read may legally return fewer bytes than requested before EOF. Treating
    // any short read as EOF truncates the scan -- the benign failure mode is a false NotFound, the
    // malign one is that a SECOND occurrence past the truncation point is never seen, so a single
    // real hit is reported when the correct answer was Ambiguous. ShortReadStream deliberately
    // hands back only a few bytes per Read call, well below whatever chunkSize/count is requested,
    // forcing PacScan to loop its own reads to fill a chunk.
    private sealed class ShortReadStream : MemoryStream
    {
        private readonly int _maxPerRead;
        public ShortReadStream(byte[] buffer, int maxPerRead) : base(buffer) => _maxPerRead = maxPerRead;

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, _maxPerRead));
    }

    [Fact]
    public void FindSingle_ShortReadsAcrossSeam_StillFindsStraddlingNeedle()
    {
        const int chunkSize = 64;
        byte[] needle = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04 };
        int needleStart = chunkSize - 3;   // straddles the chunk boundary, as the load-bearing test
        var haystack = Filler(200);
        Array.Copy(needle, 0, haystack, needleStart, needle.Length);

        using var stream = new ShortReadStream(haystack, maxPerRead: 3);
        long offset = PacScan.FindSingle(stream, needle, chunkSize);

        Assert.Equal(needleStart, offset);
    }

    [Fact]
    public void FindSingle_ShortReadsAcrossSeam_SecondOccurrencePastTruncationPoint_ReturnsAmbiguous()
    {
        const int chunkSize = 64;
        byte[] needle = { 0x11, 0x22, 0x33, 0x44 };
        var haystack = Filler(200);
        Array.Copy(needle, 0, haystack, 10, needle.Length);
        Array.Copy(needle, 0, haystack, 150, needle.Length);   // well past a truncation a short-read bug would cause

        using var stream = new ShortReadStream(haystack, maxPerRead: 5);
        long offset = PacScan.FindSingle(stream, needle, chunkSize);

        Assert.Equal(PacScan.Ambiguous, offset);
    }

    // ---- R2-2, THE non-vacuous proof for the batch API: N needles must cost ONE sequential pass
    // over the stream, not N passes. CountingStream tallies every byte handed back by Read; if
    // the batch degenerates to a per-needle loop (each re-scanning the whole stream), the total
    // would be roughly needles.Count times the stream length -- this asserts it stays close to
    // ONE stream length instead. ----

    private sealed class CountingStream : MemoryStream
    {
        public long TotalBytesRead { get; private set; }
        public CountingStream(byte[] buffer) : base(buffer) { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }
    }

    [Fact]
    public void FindBatch_MultipleNeedles_CostsOneSequentialPass_NotOnePerNeedle()
    {
        const int chunkSize = 500;
        const int haystackLen = 5000;
        var haystack = Filler(haystackLen);
        var needles = new List<byte[]>();
        var expectedOffsets = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            byte[] needle = { (byte)(0x80 + i), (byte)(0x90 + i), (byte)(0xA0 + i) };
            int at = 100 + i * 450;   // spread across the whole file, one per rough chunk
            Array.Copy(needle, 0, haystack, at, needle.Length);
            needles.Add(needle);
            expectedOffsets.Add(at);
        }

        using var stream = new CountingStream(haystack);
        var results = PacScan.FindBatch(stream, needles, chunkSize);

        for (int i = 0; i < needles.Count; i++)
            Assert.Equal(expectedOffsets[i], results[i]);

        // A naive per-needle loop (10 needles, each re-reading the ~5000-byte stream from the
        // start) would read roughly 50,000+ bytes total. One sequential pass reads roughly
        // haystackLen bytes (plus a little overlap carry per chunk) -- nowhere near that.
        Assert.True(stream.TotalBytesRead < haystackLen * 2,
            $"expected roughly one pass (~{haystackLen} bytes), got {stream.TotalBytesRead}");
    }
}
