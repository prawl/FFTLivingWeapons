using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure byte[] scanner for equip-card paint sites. Works on a buffer only, no Mem.
/// Finds "Kills: " slots and ties each to its owning weapon through the flavor line, and finds
/// weapon names with valid 2-char suffix slots. buf[0..lookback) is a prefix for anchors near the
/// window start, hits may start in [lookback, lookback+searchable), and trailing slack lets slots finish.
/// Split at a real seam. This file finds hits and owners, CardScanner.Anchors.cs holds
/// the candidate-selection and positional-search primitives.
/// </summary>
internal static partial class CardScanner
{
    public const int FlavorWindow = 2048;

    internal readonly record struct SuffixHit(int Id, int Enc, int SlotPos, int NamePos);
    internal readonly record struct KillsHit(int Id, int Enc, int SlotPos, int FlavorPos);

    /// <summary>Find "Kills: " occurrences (both encodings) and resolve each owner with
    /// <see cref="LayoutProbeOwner"/> (step 1), falling back to <see cref="FindNearestFlavor"/> (step 2).
    /// A hit needs a valid <see cref="Signatures.KillsMeterSlotChars"/>-wide meter slot and an owner.
    /// <paramref name="anchors"/> (Reliquary Phase 1, decision 12) adds earned lines to the flavor
    /// search; null searches baked flavors only.</summary>
    public static void FindKills(byte[] buf, int lookback, int searchable, CardPatterns pats,
                                 List<KillsHit> hits, EarnedAnchors? anchors = null)
    {
        int windowEnd = lookback + searchable;
        foreach (int enc in new[] { 1, 2 })
        {
            byte[] killsPattern = pats.Kills(enc);
            int slotWidth = Signatures.KillsMeterSlotChars * enc;

            var killsHits = new List<int>();
            ByteScan.FindAll(buf, killsPattern, lookback, windowEnd, killsHits);

            foreach (int killsPos in killsHits)
            {
                int slotPos = killsPos + killsPattern.Length;
                if (slotPos + slotWidth > buf.Length) continue;
                if (!ByteScan.MeterSlotDigits(buf, slotPos, enc, Signatures.KillsMeterSlotChars)) continue;

                int ownerId = LayoutProbeOwner(buf, slotPos, slotWidth, enc, pats, anchors, out int flavorPos);
                if (ownerId < 0)
                {
                    ownerId = FindNearestFlavor(buf, killsPos, enc, pats, anchors);
                    if (ownerId < 0) continue;
                    flavorPos = NearestAnchorPosBidir(buf, killsPos, AnchorCandidates(ownerId, enc, pats, anchors));
                }

                hits.Add(new KillsHit(ownerId, enc, slotPos, flavorPos));
            }
        }
    }

    /// <summary>Find weapon names (for ids in nameIds) followed by a valid 2-char suffix slot, both
    /// encodings. The name must start in the search window; the slot may run past it within buf.</summary>
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

    /// <summary>Step 1. The baked layout is fixed (Kills, meter slot, "\n", Grows line,
    /// "\n", flavor), so check the Grows line at G = slotPos + slotWidth + 1 char and the flavor at
    /// F = G + growsLen + 1 char. Same-lane weapons bake identical Grows, so each Grows match is
    /// checked at F against its own <see cref="UniqueAnchorCandidates"/>, and the match is the owner.
    /// Anchor prefix collisions are gated by analyze.py (check_flavor_prefix, not check_unique_flavor)
    /// and earned lines stay length-locked to the flavor (EarnedAnchors.TryEncode). The composed-line
    /// residual noted at EarnedAnchors.UniqueAnchorsFor is a case the gate cannot see.
    /// Returns -1 (flavorPos -1) when nothing matches, and the caller falls through to step 2.</summary>
    private static int LayoutProbeOwner(byte[] buf, int slotPos, int slotWidth, int enc,
                                        CardPatterns pats, EarnedAnchors? anchors, out int flavorPos)
    {
        flavorPos = -1;
        int g = slotPos + slotWidth + 1 * enc;
        foreach (var entry in pats.Entries)
        {
            if (entry.Enc != enc) continue;
            if (entry.Grows.Length == 0) continue;
            if (!MatchesExactlyAt(buf, g, entry.Grows)) continue;

            int f = g + entry.Grows.Length + 1 * enc;
            foreach (var cand in UniqueAnchorCandidates(entry.Id, enc, pats, anchors))
            {
                if (!MatchesExactlyAt(buf, f, cand)) continue;
                flavorPos = g;
                return entry.Id;
            }
        }
        return -1;
    }

    /// <summary>Step 2 (fallback). The weapon id whose flavor (same encoding) is nearest the position on
    /// either side (<see cref="NearestAnchorPosBidir"/>) within FlavorWindow, or -1. Runs when
    /// <see cref="LayoutProbeOwner"/> finds no owner. Old-layout buffers with no Grows line (e.g.
    /// CardScannerFindKillsTests.FindKills_old_backward_layout_two_contiguous_cards_still_resolves_identically),
    /// truncated copies, anything odd. <paramref name="anchors"/> adds earned lines; null is baked flavors only.
    /// The search has been bidirectional since 2026-07-06 (it took the largest backward position before),
    /// because nothing sits between a card's "Kills: " and its own flavor.
    /// Same-lane weapons bake identical Grows, so distance ties (dAll) go to the smaller dUnique, the nearest
    /// distance over unique candidates only; no unique candidate in-window means ineligible.
    /// Two counterexamples to that rule alone (S1. A lane-mate's adjacent flavor beats
    /// this weapon's farther Grows on dUnique; S2. A foreign short flavor beats it on dAll), which step 1 resolves.
    /// Known residual. A copy truncated right after its Grows line has no unique anchor, so a lane-mate may get the hit.</summary>
    private static int FindNearestFlavor(byte[] buf, int pos, int enc, CardPatterns pats, EarnedAnchors? anchors)
    {
        int bestDAll = int.MaxValue, bestDUnique = int.MaxValue, bestId = -1;
        foreach (var entry in pats.Entries)
        {
            if (entry.Enc != enc) continue;

            int pAll = NearestAnchorPosBidir(buf, pos, AnchorCandidates(entry.Id, enc, pats, anchors));
            if (pAll < 0) continue;
            int dAll = Math.Abs(pAll - pos);

            int pUnique = NearestAnchorPosBidir(buf, pos, UniqueAnchorCandidates(entry.Id, enc, pats, anchors));
            if (pUnique < 0) continue;   // no unique candidate in-window. Ineligible to own
            int dUnique = Math.Abs(pUnique - pos);

            if (dAll < bestDAll || (dAll == bestDAll && dUnique < bestDUnique))
            {
                bestDAll = dAll; bestDUnique = dUnique; bestId = entry.Id;
            }
        }
        return bestId;
    }
}
