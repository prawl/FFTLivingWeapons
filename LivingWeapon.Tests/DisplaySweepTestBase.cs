using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Shared fixture helpers for DisplaySweep tests.
/// </summary>
internal static class DisplaySweepTestBase
{
    internal sealed class Capture
    {
        public readonly List<(long chunkStart, int lookback, int searchable)> Chunks = new();

        /// <summary>chunkStart is absolute chunk base (addr + lookback removes the lb offset).</summary>
        public DisplaySweep.ChunkHandler Handler => (buf, lb, s, bufBase) =>
            Chunks.Add((bufBase + lb, lb, s));
    }

    internal static FakeHeap OneRegion(long baseAddr, int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i & 0xFF);
        return new FakeHeap((baseAddr, data));
    }

    /// <summary>Run Ticks with 1ms clock increments until the given region's worth of bytes
    /// has been offered (search-window total >= regionSize). Safe for small regions.</summary>
    internal static void DrainRegion(DisplaySweep sw, ref long now, Capture cap,
                                    long regionBase, int regionSize)
    {
        long covered = 0;
        int limit = regionSize / DisplaySweep.ChunkSize + 50;
        while (covered < regionSize && limit-- > 0)
        {
            now++;
            sw.Tick(DisplaySweep.ChunkSize, cap.Handler);
            covered = 0;
            foreach (var (cs, _, s) in cap.Chunks)
                if (cs >= regionBase && cs < regionBase + regionSize)
                    covered += s;
        }
    }
}
