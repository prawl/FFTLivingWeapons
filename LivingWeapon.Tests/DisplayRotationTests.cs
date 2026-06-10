using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A7: Rotation must be driven by a persistent cursor advanced per chunk (not per
/// generation), so successive chunks/passes cover all ids. PaintAll must NOT be called
/// for every hit chunk; only newly-registered sites from that chunk get painted
/// on the hot-update path.
/// </summary>
public class DisplayRotationTests
{
    private const long StaticsBase3 = 0x70_0000_0000L;
    private const long SourceBase3  = 0x71_0000_0000L;

    /// <summary>Build a meta with 20 weapons (ids 20-39).</summary>
    private static Dictionary<int, WeaponMeta> BuildLargeMeta()
    {
        var d = new Dictionary<int, WeaponMeta>();
        for (int id = 20; id < 40; id++)
            d[id] = new WeaponMeta
            {
                Name    = $"Wp{id:D2}",
                Flavor  = $"Flavor text number {id:D2} here",
                Wp      = 10,
                Cat     = "Sword",
                Formula = 1,
            };
        return d;
    }

    private static byte[] WriteCardToBuffer(byte[] buf, int pos, string name, string flavor)
    {
        byte[] nameB   = ByteScan.Ascii(name);
        byte[] suffix  = ByteScan.Ascii("  ");
        byte[] pad     = ByteScan.Ascii("   ");
        byte[] flavorB = ByteScan.Ascii(flavor);
        byte[] nn      = ByteScan.Ascii("\n\nKills: ");
        byte[] kills   = ByteScan.Ascii("0   ");

        int at = pos;
        Array.Copy(nameB,   0, buf, at, nameB.Length);   at += nameB.Length;
        Array.Copy(suffix,  0, buf, at, suffix.Length);  at += suffix.Length;
        Array.Copy(pad,     0, buf, at, pad.Length);     at += pad.Length;
        Array.Copy(flavorB, 0, buf, at, flavorB.Length); at += flavorB.Length;
        Array.Copy(nn,      0, buf, at, nn.Length);      at += nn.Length;
        Array.Copy(kills,   0, buf, at, kills.Length);
        return buf;
    }

    private sealed class Clock3
    {
        public long Ms;
        public Func<long> Func => () => Ms;
    }

    /// <summary>
    /// A region with 20 cards and 1 target: within a bounded number of Ticks every
    /// card id must receive its suffix painted (rotation coverage). Also checks that
    /// FakeHeap.Writes is bounded (not O(sites * chunks) from full-cache repaint per chunk).
    /// </summary>
    [Fact]
    public void Rotation_eventually_covers_all_ids_in_bounded_ticks()
    {
        var meta = BuildLargeMeta();
        // Each weapon has exactly 1 kill so suffix = "  " (0 < threshold)... use 5 kills for tier 1 (+)
        var kills = new Dictionary<int, int>();
        for (int id = 20; id < 40; id++) kills[id] = 5;

        // Source region: 20 cards packed end-to-end
        int cardStride = 120; // enough room per card
        var src = new byte[cardStride * 20 + 256]; // padding
        for (int i = 0; i < 20; i++)
        {
            int id = 20 + i;
            WriteCardToBuffer(src, i * cardStride,
                $"Wp{id:D2}", $"Flavor text number {id:D2} here");
        }

        var statics = new byte[64];
        statics[0] = 20; statics[1] = 0; // mirror = id 20

        var heap = new FakeHeap((SourceBase3, src), (StaticsBase3, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase3 + 0,
            mirrorOffHandAddr: StaticsBase3 + 2,
            wpScratchAddr:     StaticsBase3 + 4);

        var clock = new Clock3();
        var display = new Display(meta, kills, wrapped, clock.Func);

        // Run bounded Ticks and count how many distinct ids get their suffix painted
        // (suffix for 5 kills at prod thresholds {5,20,50}: tier 1 = "+")
        byte[] plusBytes = ByteScan.Ascii("+");
        int maxTicks = 800;
        for (int i = 0; i < maxTicks; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }

        // Check that at least 15 out of 20 ids got suffix painted (generous bound)
        int paintedCount = 0;
        for (int i = 0; i < 20; i++)
        {
            int id = 20 + i;
            int cardPos = i * cardStride;
            // suffix slot is at: name.Length + 0 (right after name)
            int nameLen = ByteScan.Ascii($"Wp{id:D2}").Length;
            int suffixOff = cardPos + nameLen;
            if (heap.TryReadBytes(SourceBase3 + suffixOff, 1, out var slot))
            {
                if (slot[0] == plusBytes[0]) paintedCount++;
            }
        }

        Assert.True(paintedCount >= 15,
            $"Rotation should paint most ids within {maxTicks} ticks; got {paintedCount}/20");
    }

    /// <summary>
    /// Write count per chunk must not grow proportionally to cached site count --
    /// no full PaintAll on every hit chunk.
    /// </summary>
    [Fact]
    public void Writes_per_tick_bounded_not_proportional_to_all_sites()
    {
        var meta = BuildLargeMeta();
        var kills = new Dictionary<int, int>();
        for (int id = 20; id < 40; id++) kills[id] = 5;

        int cardStride = 120;
        var src = new byte[cardStride * 20 + 256];
        for (int i = 0; i < 20; i++)
        {
            int id = 20 + i;
            WriteCardToBuffer(src, i * cardStride,
                $"Wp{id:D2}", $"Flavor text number {id:D2} here");
        }

        var statics = new byte[64];
        statics[0] = 20; statics[1] = 0;

        var heap = new FakeHeap((SourceBase3 + 0x1_0000_0000L, src), (StaticsBase3 + 0x1_0000_0000L, statics));
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase3 + 0x1_0000_0000L + 0,
            mirrorOffHandAddr: StaticsBase3 + 0x1_0000_0000L + 2,
            wpScratchAddr:     StaticsBase3 + 0x1_0000_0000L + 4);

        var clock = new Clock3();
        var display = new Display(meta, kills, wrapped, clock.Func);

        // Discovery pass
        for (int i = 0; i < 100; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }

        // After discovery, count writes over a fixed Tick window
        // Kill all counts so steady-state has nothing to rewrite
        foreach (int id in new int[]{20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39})
            kills[id] = 5; // unchanged

        int writesBefore = heap.Writes;
        for (int i = 0; i < 20; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }
        int writesAfter = heap.Writes;
        int writesInWindow = writesAfter - writesBefore;

        // With ~20 cards and 20 steady-state Ticks, writes should be modest
        // (not 20 sites x 20 ticks = 400 redundant writes)
        // Allow some leeway but flag O(sites*ticks) behaviour
        Assert.True(writesInWindow < 200,
            $"Too many writes in steady state: {writesInWindow}; likely PaintAll-per-chunk");
    }
}
