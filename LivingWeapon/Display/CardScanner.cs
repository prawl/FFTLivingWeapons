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
///
/// LW-332 split this into two files at a real seam (this class was already near the 200-line
/// refactor trigger before the Grows-anchor arc): this file is the hit-finding ORCHESTRATION
/// (FindKills/FindSuffixes/LayoutProbeOwner/FindNearestFlavor -- what a hit is and who owns it,
/// STEP 1 exact-layout probe then STEP 2 nearest-distance fallback, round 2 of the arc);
/// CardScanner.Anchors.cs is the candidate-selection + positional-search PRIMITIVES
/// (AnchorCandidates/UniqueAnchorCandidates/NearestAnchorPos*/MatchesExactlyAt) those methods
/// call into. Nothing in either half depends on the other's internals beyond ordinary method
/// calls.
/// </summary>
internal static partial class CardScanner
{
    public const int FlavorWindow = 2048;

    internal readonly record struct SuffixHit(int Id, int Enc, int SlotPos, int NamePos);
    internal readonly record struct KillsHit(int Id, int Enc, int SlotPos, int FlavorPos);

    /// <summary>Find "Kills: " occurrences (both encodings) and resolve each hit's owner via
    /// <see cref="LayoutProbeOwner"/> (STEP 1, the exact-layout probe) falling through to
    /// <see cref="FindNearestFlavor"/> (STEP 2, nearest-distance) only when the probe finds no
    /// owner -- see LayoutProbeOwner's doc for why STEP 1 must run first. Validate the
    /// <see cref="Signatures.KillsMeterSlotChars"/>-wide meter slot. Emit a hit only if an owner
    /// is found. No hit if the "Kills: " starts outside [lookback, searchable). <paramref
    /// name="anchors"/> (Reliquary Phase 1, decision 12) extends the flavor search to ANY of a
    /// weapon's registered anchors (baked, current, or previous earned line); null (every
    /// pre-Reliquary caller) searches baked flavors only, byte-identical to before.</summary>
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

    /// <summary>STEP 1 (LW-332 round 2): the exact-layout probe. The bake is FIXED (Kills
    /// literal, meter slot, "\n", Grows line, "\n", flavor), so rather than asking "what's
    /// nearest" (STEP 2's question -- see FindNearestFlavor's doc for the S1/S2 bugs that
    /// produced), this asks a yes/no question at the FIXED offsets the bake guarantees: is MY
    /// Grows line at G, and is MY flavor (or a same-length earned line) at F? G = slotPos +
    /// slotWidth + 1 char ("\n"); F = G + growsLen + 1 char (the second "\n"). Every entry (same
    /// enc) whose Grows pattern matches the bytes exactly at G is a lane-mate candidate
    /// (same-lane weapons bake byte-IDENTICAL Grows, so more than one entry can match here); each
    /// is then checked at F against its OWN unique anchors (<see cref="UniqueAnchorCandidates"/>
    /// -- EarnedAnchors.UniqueAnchorsFor when wired, else the baked Flavor). No anchor-PREFIX
    /// collision reaches F: gate-enforced by analyze.py's FLAVOR PREFIX section
    /// (check_flavor_prefix), NOT check_unique_flavor (equality-only -- round 2's mistaken
    /// credit); earned lines stay length-locked to the baked flavor (EarnedAnchors.TryEncode).
    /// That enforcement lives at the data gate on purpose: this probe stays deliberately
    /// unhardened (no longest-match/terminator logic). For BAKED anchors that gate guarantees at
    /// most one candidate can match at F -- the composed-line residual documented beside
    /// EarnedAnchors.UniqueAnchorsFor is the one exception the gate cannot see. Whichever
    /// candidate matches is treated as the owner, FlavorPos = G. Returns -1 (flavorPos -1 too)
    /// when no lane-mate matches at G, or none passes the F check -- callers fall through to
    /// STEP 2.</summary>
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

    /// <summary>STEP 2 (fallback): find the weapon id whose flavor (same encoding) is NEAREST the
    /// given position on EITHER side (minimum absolute distance: see
    /// <see cref="NearestAnchorPosBidir"/>), or -1 if none found within FlavorWindow on either
    /// side. Runs only when <see cref="LayoutProbeOwner"/> (STEP 1) finds no owner -- old-layout
    /// buffers (no baked Grows line at all: every pre-Grows fixture, e.g.
    /// CardScannerFindKillsTests.FindKills_old_backward_layout_two_contiguous_cards_still_resolves_identically),
    /// truncated copies, and anything exotic. <paramref name="anchors"/> extends the search to a
    /// weapon's registered earned lines too (decision 12); null falls back to baked-flavor-only,
    /// byte-identical to the pre-Reliquary behavior.
    ///
    /// KEY DESIGN CHANGE (2026-07-06): this used to pick the LARGEST backward-only position.
    /// Bidirectional is a strict generalization: nothing is ever allocated between a card's
    /// "Kills: " and its OWN flavor (fixed slot + "\n\n"), so the owner's flavor is the nearest
    /// candidate whether it sits above (every pre-existing fixture) or below (the new bake).
    ///
    /// TWO-KEY OWNERSHIP (LW-332 round 1, KEPT here as the fallback): same-lane weapons bake
    /// byte-IDENTICAL Grows patterns, so two entries can report the same nearest distance (dAll)
    /// by matching the SAME physical occurrence -- a coin-flip on iteration order would decide
    /// the "winner". Each entry also computes dUnique, its nearest distance over its UNIQUE
    /// candidates only (<see cref="UniqueAnchorCandidates"/>: flavor + earned lines, excluding
    /// Grows); no unique candidate in-window makes an entry INELIGIBLE (a shared Grows byte match
    /// must never confer ownership alone). Winner minimizes (dAll, dUnique) lexicographically.
    /// LW-332 round 2 found this rule ALONE still loses two executed counterexamples (S1: a
    /// same-lane neighbor's own adjacent flavor out-nears this weapon's own farther Grows anchor
    /// on the dUnique tiebreak; S2: a foreign shorter-than-the-fixed-gap flavor out-nears this
    /// weapon's own Grows anchor on dAll outright) -- STEP 1 now runs first and resolves both;
    /// this rule survives only as the STEP 2 fallback.
    ///
    /// KNOWN ACCEPTED RESIDUAL (fallback-only, pre-existing, unchanged by this arc): a buffer
    /// copy truncated right after its own Grows line has no unique anchor, so it is ineligible
    /// here and the fallback may attribute its Kills hit to a lane-mate instead of dropping it.
    /// The layout probe never fires on such a copy (its own flavor isn't at F either), so this
    /// stays fallback-only.</summary>
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
            if (pUnique < 0) continue;   // no unique candidate in-window: ineligible to own
            int dUnique = Math.Abs(pUnique - pos);

            if (dAll < bestDAll || (dAll == bestDAll && dUnique < bestDUnique))
            {
                bestDAll = dAll; bestDUnique = dUnique; bestId = entry.Id;
            }
        }
        return bestId;
    }
}
