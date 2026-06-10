using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure byte[] scanner for equip-card paint sites. No Mem, no IGameMemory; all ops are on a buffer.
/// Scans for "Kills: " slots tied to their nearest preceding flavor (within FlavorWindow) to defeat
/// cross-attribution bugs, and for weapon names with valid 2-char suffix slots.
/// Buffer layout contract: buf[0..lookback) are the lookback prefix (for anchors near window start);
/// buf[lookback..lookback+searchable) is the search window where hits may START; bytes after
/// that are trailing slack where slots/needles may FINISH. All returned positions are in the
/// search window [lookback, lookback+searchable).
/// </summary>
internal static class CardScanner
{
    public const int FlavorWindow = 2048;

    internal readonly record struct SuffixHit(int Id, int Enc, int SlotPos, int NamePos);
    internal readonly record struct KillsHit(int Id, int Enc, int SlotPos, int FlavorPos);

    /// <summary>Find "Kills: " occurrences (both encodings) and tie each to the nearest preceding
    /// flavor (same encoding). Validate the 4-char slot. Emit a hit only if an owner flavor is
    /// found within FlavorWindow. No hit if the "Kills: " starts outside [lookback, searchable).</summary>
    public static void FindKills(byte[] buf, int lookback, int searchable, CardPatterns pats,
                                 List<KillsHit> hits)
    {
        int windowEnd = lookback + searchable;
        foreach (int enc in new[] { 1, 2 })
        {
            byte[] killsPattern = pats.Kills(enc);
            int slotWidth = 4 * enc;

            var killsHits = new List<int>();
            ByteScan.FindAll(buf, killsPattern, lookback, windowEnd, killsHits);

            foreach (int killsPos in killsHits)
            {
                int slotPos = killsPos + killsPattern.Length;
                if (slotPos + slotWidth > buf.Length) continue;
                if (!ByteScan.KillsDigits(buf, slotPos, enc)) continue;

                int ownerId = FindNearestFlavor(buf, killsPos, enc, pats);
                if (ownerId < 0) continue;

                var entry = default(CardPatterns.Entry);
                if (!pats.TryGet(ownerId, enc, out entry)) continue;

                int flavorPos = -1;
                if (entry.Flavor.Length > 0)
                {
                    int searchStart = Math.Max(0, killsPos - FlavorWindow);
                    int rel = buf.AsSpan(searchStart, killsPos - searchStart)
                        .LastIndexOf(entry.Flavor.AsSpan());
                    if (rel >= 0) flavorPos = searchStart + rel;
                }

                hits.Add(new KillsHit(ownerId, enc, slotPos, flavorPos));
            }
        }
    }

    /// <summary>Find weapon names (for ids in nameIds) + valid 2-char suffix slots (both encodings).
    /// Emit a hit for each (id, enc, name, slot) combination found where name starts in the
    /// search window and the slot is valid. Slot may extend past the window if it fits in buf.</summary>
    public static void FindSuffixes(byte[] buf, int lookback, int searchable, CardPatterns pats,
                                    IReadOnlyCollection<int> nameIds, List<SuffixHit> hits)
    {
        int windowEnd = lookback + searchable;
        foreach (int id in nameIds)
        {
            foreach (int enc in new[] { 1, 2 })
            {
                if (!pats.TryGet(id, enc, out var entry)) continue;
                if (entry.Name.Length == 0) continue;

                int slotWidth = 2 * enc;
                // Hoist the per-enc slots list outside the hit loop to avoid a copy per name hit.
                var slots = pats.Slots(enc);
                var nameHits = new List<int>();
                ByteScan.FindAll(buf, entry.Name, lookback, windowEnd, nameHits);

                foreach (int namePos in nameHits)
                {
                    int slotPos = namePos + entry.Name.Length;
                    if (slotPos + slotWidth > buf.Length) continue;
                    if (!ByteScan.MatchesAny(buf, slotPos, slots, slotWidth)) continue;

                    hits.Add(new SuffixHit(id, enc, slotPos, namePos));
                }
            }
        }
    }

    /// <summary>Find the weapon id whose flavor (same encoding) is nearest and before the
    /// given position within FlavorWindow, or -1 if none found.</summary>
    private static int FindNearestFlavor(byte[] buf, int pos, int enc, CardPatterns pats)
    {
        int bestPos = -1, bestId = -1;
        int searchStart = Math.Max(0, pos - FlavorWindow);
        if (searchStart >= pos) return -1;

        foreach (var entry in pats.Entries)
        {
            if (entry.Enc != enc || entry.Flavor.Length == 0) continue;
            var searchSpan = buf.AsSpan(searchStart, pos - searchStart);
            int rel = searchSpan.LastIndexOf(entry.Flavor.AsSpan());
            if (rel < 0) continue;
            int absPos = searchStart + rel;
            if (absPos > bestPos) { bestPos = absPos; bestId = entry.Id; }
        }

        return bestId;
    }
}
