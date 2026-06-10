using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A4: The shared read buffer must not carry stale bytes past the valid read length
/// when a shorter chunk follows a longer one.
/// </summary>
public class DisplaySweepStaleBufferTests
{
    /// <summary>
    /// Sweep two consecutive chunks where the second is shorter than the first.
    /// The bytes immediately past readLen in the offered buffer must be zero,
    /// not stale bytes from the previous (longer) read.
    /// </summary>
    [Fact]
    public void Short_read_does_not_expose_stale_tail_bytes()
    {
        // Region 1: exactly one chunk worth (fills _buf to capacity); data = 0xAA
        long base1 = 0x40_0000_0000L;
        int size1 = DisplaySweep.ChunkSize;
        var data1 = new byte[size1];
        for (int i = 0; i < size1; i++) data1[i] = 0xAA;

        // Region 2: shorter than a chunk (256 bytes); data = 0xBB
        long base2 = 0x41_0000_0000L;
        int size2 = 256;
        var data2 = new byte[size2];
        for (int i = 0; i < size2; i++) data2[i] = 0xBB;

        var heap = new FakeHeap((base1, data1), (base2, data2));
        long now = 0;
        var sw = new DisplaySweep(heap, () => now);

        // Collect (chunkStart, searchable, the 512 bytes immediately past searchable)
        var tailCaptures = new List<(long chunkStart, byte[] tail)>();

        sw.Tick(long.MaxValue, (buf, lb, searchable, bufBase) =>
        {
            long chunkStart = bufBase + lb;
            // Capture the 512 bytes immediately following the searchable window
            var tail = new byte[512];
            int start = lb + searchable;
            int toCopy = System.Math.Min(512, buf.Length - start);
            if (toCopy > 0)
                System.Array.Copy(buf, start, tail, 0, toCopy);
            tailCaptures.Add((chunkStart, tail));
        });

        // Find the capture for base2 (the short region)
        byte[]? shortTail = null;
        foreach (var (cs, tail) in tailCaptures)
        {
            if (cs == base2)
            {
                shortTail = tail;
                break;
            }
        }

        Assert.NotNull(shortTail);
        // Every tail byte must be zero (cleared), not 0xAA from the previous longer read
        bool hasStale = false;
        for (int i = 0; i < shortTail!.Length; i++)
            if (shortTail[i] != 0) { hasStale = true; break; }

        Assert.False(hasStale, "Stale bytes from previous longer read found in tail past readLen");
    }
}
