using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure byte[] test for whether a buffer is the baked pool. Reuses CardScanner.FindKills.
/// A buffer is pool when a "Kills: " hit tied to its flavor also has the owner weapon's name within NameWindow.
/// The name check matters because a transient render copy carries flavor + Kills with no name (observed 2026-07-08);
/// about 26 such lower-addressed copies otherwise win PoolLocator's first-wins tiebreak and the real pool never paints.
/// Preferring a buffer with several distinct weapon ids is PoolLocator's job.
/// </summary>
internal static class PoolLocatorPolicy
{
    /// <summary>How far from a "Kills: " hit the owner's baked name must appear. The pool puts it about 0x40 ahead;
    /// the descriptor region has none within 0x200 (observed 2026-07-08). 512 clears both with margin.</summary>
    internal const int NameWindow = 512;

    /// <summary>One window's result. IsPool, the distinct weapon ids attributed, and the hits with SlotPos/FlavorPos from CardScanner.FindKills.</summary>
    internal readonly record struct ScanResult(bool IsPool, int DistinctWeaponCount,
                                                IReadOnlyList<CardScanner.KillsHit> Hits);

    /// <summary>Scans one window with CardScanner.FindKills' lookback/searchable convention, so ChunkReader.ReadInRegion output plugs in.
    /// Hits without the owner name in NameWindow are dropped as render copies.</summary>
    internal static ScanResult Scan(byte[] buf, int lookback, int searchable, CardPatterns pats)
    {
        var hits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, pats, hits);

        var ids = new HashSet<int>();
        var baked = new List<CardScanner.KillsHit>();
        var nameHits = new List<int>();
        foreach (var h in hits)
        {
            if (!pats.TryGet(h.Id, h.Enc, out var pat) || pat.Name.Length == 0) continue;
            int lo = h.SlotPos - NameWindow; if (lo < 0) lo = 0;
            int hi = h.SlotPos + NameWindow; if (hi > buf.Length) hi = buf.Length;
            nameHits.Clear();
            ByteScan.FindAll(buf, pat.Name, lo, hi, nameHits);
            if (nameHits.Count == 0) continue;   // no adjacent name, so a render copy
            baked.Add(h);
            ids.Add(h.Id);
        }

        return new ScanResult(baked.Count > 0, ids.Count, baked);
    }

    /// <summary>Buffer-relative address of the meter body, right after the "Kills: " literal.</summary>
    internal static int SlotOffset(CardScanner.KillsHit hit) => hit.SlotPos;

    /// <summary>Buffer-relative address of the owner's flavor text, before or after the Kills hit.</summary>
    internal static int AnchorOffset(CardScanner.KillsHit hit) => hit.FlavorPos;
}
