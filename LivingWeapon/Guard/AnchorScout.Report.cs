using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Console narration for <see cref="AnchorScout"/>. Turns an <see cref="AnchorScan"/>
/// verdict into a console sentence plus a trace line (<see cref="ModLogger.EventWithTrace"/>,
/// <see cref="LogVerb.Startup"/>) and builds the summary line. Console text is hex-free
/// (LogContractTests); hex detail goes in the trace.
/// </summary>
internal sealed partial class AnchorScout
{
    private const int MaxAmbiguousBasesListed = 8;
    // RosterBase 0x1411A7D10, InventoryCountBase 0x1411A7C00 = 0x110, stable across pre-1.5/1.5/1.5.1
    // (Offsets.cs:103,353; docs/research/PORT_1.5_OFFSETS.md:57-58), so a roster find predicts the
    // inventory base.
    private const long InventoryCountDelta = 0x110;

    private void LogVerdict(AnchorSpec spec, AnchorScan scan)
    {
        switch (scan.Verdict)
        {
            case AnchorVerdict.FoundAtPin:
                ModLogger.EventWithTrace(LogVerb.Startup,
                    $"The anchor {spec.Name} was found at its pinned address.",
                    $"anchor {spec.Name}: found at pin 0x{spec.PinnedAddress:X} (region 0x{spec.RegionLo:X}-0x{spec.RegionHi:X}, {scan.UnreadableChunks} unreadable chunks)");
                MaybeLogSibling(spec, scan);
                break;
            case AnchorVerdict.FoundElsewhere:
                LogFoundElsewhere(spec, scan);
                MaybeLogSibling(spec, scan);
                break;
            case AnchorVerdict.Ambiguous:
                LogAmbiguous(spec, scan);
                break;
            case AnchorVerdict.NotFound:
                ModLogger.EventWithTrace(LogVerb.Startup,
                    $"The anchor {spec.Name} was not found in its search window.",
                    $"anchor {spec.Name}: not found (region 0x{spec.RegionLo:X}-0x{spec.RegionHi:X}, {scan.UnreadableChunks} unreadable chunks)");
                break;
        }
    }

    private static void LogFoundElsewhere(AnchorSpec spec, AnchorScan scan)
    {
        long baseAddr = scan.Bases[0];
        long delta = baseAddr - spec.PinnedAddress;
        string sign = delta >= 0 ? "+" : "-";
        ModLogger.EventWithTrace(LogVerb.Startup,
            $"The anchor {spec.Name} was found AWAY from its pinned address; the re-find inventory has the new location.",
            $"anchor {spec.Name}: found elsewhere at 0x{baseAddr:X} (pin 0x{spec.PinnedAddress:X}, delta {sign}0x{Math.Abs(delta):X})");
    }

    private static void LogAmbiguous(AnchorSpec spec, AnchorScan scan)
    {
        var listed = new List<string>();
        for (int i = 0; i < scan.Bases.Count && i < MaxAmbiguousBasesListed; i++)
            listed.Add($"0x{scan.Bases[i]:X}");
        ModLogger.EventWithTrace(LogVerb.Startup,
            $"The anchor {spec.Name} matched more than one location; the re-find inventory lists every candidate.",
            $"anchor {spec.Name}: ambiguous (bases {string.Join(", ", listed)}, {scan.Bases.Count} total)");
    }

    /// <summary>Only the roster-base spec has a sibling to predict; runs after FoundAtPin/FoundElsewhere.</summary>
    private static void MaybeLogSibling(AnchorSpec spec, AnchorScan scan)
    {
        if (spec.Name != RosterBaseSpecName) return;
        long inventoryBase = scan.Bases[0] - InventoryCountDelta;
        ModLogger.EventWithTrace(LogVerb.Startup,
            "The roster find predicts the inventory-count table (vanilla block) as a sibling.",
            $"sibling prediction: inventory-count base at 0x{inventoryBase:X} (roster - 0x110; delta held across pre-1.5/1.5/1.5.1)");
    }

    /// <summary>Logs the summary once every spec has concluded, and again if the combination changes
    /// (a rescan upgrade).</summary>
    private void MaybeLogSummary()
    {
        foreach (var scan in _scans)
            if (scan.Verdict == AnchorVerdict.Scanning) return;

        var current = new AnchorVerdict[_scans.Length];
        for (int i = 0; i < _scans.Length; i++) current[i] = _scans[i].Verdict;
        if (_lastSummary != null && VerdictsEqual(current, _lastSummary)) return;
        _lastSummary = current;

        int atPin = 0, elsewhere = 0, ambiguous = 0, notFound = 0;
        foreach (var v in current)
        {
            switch (v)
            {
                case AnchorVerdict.FoundAtPin: atPin++; break;
                case AnchorVerdict.FoundElsewhere: elsewhere++; break;
                case AnchorVerdict.Ambiguous: ambiguous++; break;
                case AnchorVerdict.NotFound: notFound++; break;
            }
        }
        ModLogger.EventWithTrace(LogVerb.Startup,
            "The anchor scout finished its re-find inventory; this is the starting map for a re-anchor.",
            $"anchor scout: {atPin} at pin, {elsewhere} elsewhere, {ambiguous} ambiguous, {notFound} not found");
    }

    private static bool VerdictsEqual(AnchorVerdict[] a, AnchorVerdict[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
