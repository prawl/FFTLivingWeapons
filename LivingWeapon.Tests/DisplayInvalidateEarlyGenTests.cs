using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A6: When the target set changes (equip screen opened/changed), Display must call
/// Invalidate on the sweep so a new generation starts after GenerationMinGapMs --
/// not waiting up to GenerationRestMs (90s).
/// </summary>
public class DisplayInvalidateEarlyGenTests
{
    private const long StaticsBase2 = 0x60_0000_0000L;
    private const long SourceBase2  = 0x61_0000_0000L;

    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 10, new WeaponMeta { Name = "BowX", Flavor = "Fletched with regret", Wp = 8, Cat = "Bow", Formula = 1 } },
        { 11, new WeaponMeta { Name = "BowY", Flavor = "Arrow never sleeps", Wp = 9, Cat = "Bow", Formula = 1 } },
    };

    private static byte[] EncodeCard(string name, string flavor)
    {
        // name + "  " + "   " + flavor + "\n\nKills: " + the unpainted meter placeholder
        var parts = new List<byte[]>
        {
            ByteScan.Ascii(name),
            ByteScan.Ascii("  "),
            ByteScan.Ascii("   "),
            ByteScan.Ascii(flavor),
            ByteScan.Ascii("\n\nKills: "),
            ByteScan.Ascii(Signatures.KillsMeterSlot(0)),
        };
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var buf = new byte[total];
        int pos = 0;
        foreach (var p in parts) { Array.Copy(p, 0, buf, pos, p.Length); pos += p.Length; }
        return buf;
    }

    /// <summary>
    /// After a completed generation, add a new region with a card, change the mirror
    /// target, and advance past GenerationMinGapMs (but far short of GenerationRestMs).
    /// Tick must discover and paint the new card without waiting for the full rest gap.
    /// </summary>
    [Fact]
    public void Target_change_triggers_early_generation_after_min_gap()
    {
        var meta  = BuildMeta();
        var kills = new Dictionary<int, int> { { 10, 7 }, { 11, 0 } };
        var clock = new TestClock();

        // Initial source region: card for id 10
        var src = EncodeCard("BowX", "Fletched with regret");
        var padded = new byte[DisplaySweep.ChunkSize];
        Array.Copy(src, 0, padded, 0, src.Length);

        // Statics region: MirrorWeapon=10, MirrorOffHand=0, WpScratch=8
        var statics = new byte[64];
        statics[0] = 10; statics[1] = 0;
        statics[2] = 0;  statics[3] = 0;
        statics[4] = 8;

        var heap = new FakeHeap(
            (SourceBase2,  padded),
            (StaticsBase2, statics));

        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  StaticsBase2 + 0,
            mirrorOffHandAddr: StaticsBase2 + 2,
            wpScratchAddr:     StaticsBase2 + 4);

        var display = new Display(meta, kills, wrapped, clock.Func);

        // Drain initial generation (mirror = 10)
        for (int i = 0; i < 200; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }

        // Now add a NEW source region with card for id 11
        var src2 = EncodeCard("BowY", "Arrow never sleeps");
        var padded2 = new byte[512];
        Array.Copy(src2, 0, padded2, 0, src2.Length);
        long newRegionBase = 0x62_0000_0000L;
        heap.AddRegion(newRegionBase, padded2, writable: true);
        kills[11] = 5;

        // Change mirror target to id 11
        statics[0] = 11; statics[1] = 0;

        // Advance past GenerationMinGapMs (but far less than GenerationRestMs)
        clock.Ms += DisplaySweep.GenerationMinGapMs + 10;

        // Run Ticks: the new generation must start and paint the new card
        bool painted = false;
        byte[] expected = ByteScan.Ascii(Signatures.KillsMeterSlot(5));
        for (int i = 0; i < 300 && !painted; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);

            // Search the new region for the painted kills=5 meter body.
            var region = heap.RegionBytes(newRegionBase);
            if (region != null)
            {
                for (int j = 0; j <= region.Length - expected.Length; j++)
                {
                    bool match = true;
                    for (int k = 0; k < expected.Length; k++)
                        if (region[j + k] != expected[k]) { match = false; break; }
                    if (match) { painted = true; break; }
                }
            }
        }

        Assert.True(painted,
            "New card in new region must be discovered and painted well before GenerationRestMs");
    }
}
