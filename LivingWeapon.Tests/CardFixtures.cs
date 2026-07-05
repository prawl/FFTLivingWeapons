using System;
using System.Collections.Generic;
using LivingWeapon;

namespace LivingWeapon.Tests;

/// <summary>
/// Shared card-byte fixtures for the Display/CardScanner/CardSites suites. WriteCard is
/// the canonical card-block encoder; the pad/filler knobs preserve each suite's exact
/// byte layout (the Display suites pad name->flavor with "   ", the scanner suites append
/// " filler." after the flavor instead) -- those differences are real layout knowledge,
/// not noise. WriteKillsBlock is the minimal anchor+slot shape the CardSites suites use.
/// </summary>
internal static class CardFixtures
{
    /// <summary>Encode a single card block at pos in buf.
    /// Layout: Name + "  " (suffix slot) + pad + Flavor + filler + "\n\nKills: " + "0   " (kills slot).
    /// Returns (suffixPos, flavorPos, killsSlotPos) all buf-relative.</summary>
    internal static (int suffixPos, int flavorPos, int killsSlotPos) WriteCard(
        byte[] buf, int pos, string name, string flavor, int enc = 1,
        string pad = "   ", string filler = "")
    {
        byte[] nameB   = ByteScan.Enc(name, enc);
        byte[] sufB    = ByteScan.Enc("  ", enc);
        byte[] padB    = ByteScan.Enc(pad, enc);
        byte[] flvB    = ByteScan.Enc(flavor, enc);
        byte[] fillB   = ByteScan.Enc(filler, enc);
        byte[] nnB     = ByteScan.Enc("\n\nKills: ", enc);
        byte[] kB      = ByteScan.Enc("0   ", enc);

        int at = pos;
        Array.Copy(nameB, 0, buf, at, nameB.Length); at += nameB.Length;
        int suffixPos = at;
        Array.Copy(sufB,  0, buf, at, sufB.Length);  at += sufB.Length;
        Array.Copy(padB,  0, buf, at, padB.Length);  at += padB.Length;
        int flavorPos = at;
        Array.Copy(flvB,  0, buf, at, flvB.Length);  at += flvB.Length;
        Array.Copy(fillB, 0, buf, at, fillB.Length); at += fillB.Length;
        Array.Copy(nnB,   0, buf, at, nnB.Length);   at += nnB.Length;
        int killsSlotPos = at;
        Array.Copy(kB,    0, buf, at, kB.Length);
        return (suffixPos, flavorPos, killsSlotPos);
    }

    /// <summary>Write a minimal kills block: flavor anchor at anchorPos, a gap of untouched
    /// bytes, then "Kills: " + the 4-char slot (default the unpainted "0   " placeholder).
    /// Returns the buf-relative slot position.</summary>
    internal static int WriteKillsBlock(byte[] buf, int anchorPos, string flavor, int gap,
                                        int enc = 1, string slot = "0   ")
    {
        byte[] flavorB = ByteScan.Enc(flavor, enc);
        byte[] prefixB = ByteScan.Enc("Kills: ", enc);
        byte[] slotB   = ByteScan.Enc(slot, enc);
        Array.Copy(flavorB, 0, buf, anchorPos, flavorB.Length);
        int killsPos = anchorPos + flavorB.Length + gap;
        Array.Copy(prefixB, 0, buf, killsPos, prefixB.Length);
        int slotPos = killsPos + prefixB.Length;
        Array.Copy(slotB, 0, buf, slotPos, slotB.Length);
        return slotPos;
    }

    /// <summary>Build a Display wired to a FakeHeap via OffsetRemapMem: the three Display
    /// statics (MirrorWeapon, MirrorOffHand, WpScratch) live at staticsBase + 0/2/4.
    /// <paramref name="legends"/> (Reliquary Phase 1) is optional -- null (the default) omits
    /// card-story composing, matching every pre-Reliquary caller of this fixture.</summary>
    internal static Display MakeDisplay(Dictionary<int, WeaponMeta> meta,
                                        Dictionary<int, int> kills,
                                        FakeHeap heap,
                                        long staticsBase,
                                        TestClock clock,
                                        LegendStore? legends = null)
    {
        var wrapped = new OffsetRemapMem(heap,
            mirrorWeaponAddr:  staticsBase,
            mirrorOffHandAddr: staticsBase + 2,
            wpScratchAddr:     staticsBase + 4);
        return new Display(meta, kills, wrapped, clock.Func, legends);
    }

    /// <summary>Advance the clock and call Tick until the generation is complete or maxTicks
    /// is reached. Advances by HotRescanMs+1 per tick to keep hot chunks live.</summary>
    internal static void DrainGeneration(Display display, TestClock clock, int maxTicks)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            clock.Ms += DisplaySweep.HotRescanMs + 1;
            display.Tick(false);
        }
    }
}
