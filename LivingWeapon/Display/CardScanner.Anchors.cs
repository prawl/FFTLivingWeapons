using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// CardScanner's anchor-candidate-selection + positional-search half (LW-332, the 200-line seam:
/// CardScanner.cs was already near the trigger before the Grows-anchor arc). Given a weapon id
/// and encoding, AnchorCandidates/UniqueAnchorCandidates decide WHICH byte patterns count as that
/// weapon's anchor; NearestAnchorPos/NearestAnchorPosForward/NearestAnchorPosBidir are the pure
/// "nearest within a window" byte-search primitives STEP 2 (FindNearestFlavor) searches with;
/// MatchesExactlyAt (round 2) is the "exact match at one fixed position" primitive STEP 1
/// (LayoutProbeOwner) probes with instead. CardScanner.cs's FindKills/FindSuffixes/
/// LayoutProbeOwner/FindNearestFlavor are the only callers.
/// </summary>
internal static partial class CardScanner
{
    /// <summary>The byte patterns to search for weapon id's flavor/story anchor at this
    /// encoding: EarnedAnchors' full set (baked + current + previous + Grows) when wired, else
    /// the baked Flavor and Grows patterns (LW-332; either may be empty, in which case
    /// NearestAnchorPos correctly finds nothing for it).</summary>
    private static IReadOnlyList<byte[]> AnchorCandidates(int id, int enc, CardPatterns pats, EarnedAnchors? anchors)
    {
        if (anchors != null) return anchors.AnchorsFor(id, enc);
        if (!pats.TryGet(id, enc, out var entry)) return Array.Empty<byte[]>();
        var list = new List<byte[]>(2);
        if (entry.Flavor.Length > 0) list.Add(entry.Flavor);
        if (entry.Grows.Length > 0) list.Add(entry.Grows);
        return list;
    }

    /// <summary>LW-332: the SAME set as <see cref="AnchorCandidates"/> minus the Grows pattern --
    /// same-lane weapons bake byte-IDENTICAL Grows lines, so a shared Grows byte match can never
    /// distinguish them; FindNearestFlavor's two-key rule uses this set as each entry's UNIQUE
    /// (disambiguating) candidates.</summary>
    private static IReadOnlyList<byte[]> UniqueAnchorCandidates(int id, int enc, CardPatterns pats, EarnedAnchors? anchors)
    {
        if (anchors != null) return anchors.UniqueAnchorsFor(id, enc);
        return pats.TryGet(id, enc, out var entry) && entry.Flavor.Length > 0
            ? new[] { entry.Flavor }
            : Array.Empty<byte[]>();
    }

    /// <summary>Nearest occurrence (the LARGEST absolute position &lt; pos) of ANY of the given
    /// candidate byte patterns within [pos-FlavorWindow, pos), or -1 if none found.</summary>
    private static int NearestAnchorPos(byte[] buf, int pos, IReadOnlyList<byte[]> candidates)
    {
        int searchStart = Math.Max(0, pos - FlavorWindow);
        if (searchStart >= pos || candidates.Count == 0) return -1;
        var span = buf.AsSpan(searchStart, pos - searchStart);
        int best = -1;
        foreach (var cand in candidates)
        {
            if (cand.Length == 0) continue;
            int rel = span.LastIndexOf(cand.AsSpan());
            if (rel < 0) continue;
            int abs = searchStart + rel;
            if (abs > best) best = abs;
        }
        return best;
    }

    /// <summary>Nearest occurrence (the SMALLEST absolute position &gt; pos: the forward twin of
    /// <see cref="NearestAnchorPos"/>, NOT a copy-paste of its max) of ANY of the given candidate
    /// byte patterns within (pos, pos+FlavorWindow], or -1 if none found. Backs the new deployed
    /// equip-meter layout (Kills line first, flavor below).</summary>
    private static int NearestAnchorPosForward(byte[] buf, int pos, IReadOnlyList<byte[]> candidates)
    {
        int searchStart = pos + 1;
        int searchEnd = Math.Min(buf.Length, pos + FlavorWindow);
        if (searchStart >= searchEnd || candidates.Count == 0) return -1;
        var span = buf.AsSpan(searchStart, searchEnd - searchStart);
        int best = -1;
        foreach (var cand in candidates)
        {
            if (cand.Length == 0) continue;
            int rel = span.IndexOf(cand.AsSpan());
            if (rel < 0) continue;
            int abs = searchStart + rel;
            if (best < 0 || abs < best) best = abs;
        }
        return best;
    }

    /// <summary>True if buf[pos..pos+pattern.Length) equals pattern exactly (bounds-checked both
    /// ends). LW-332 round 2's exact-layout probe primitive: unlike every NearestAnchorPos*
    /// method above (which searches a window for the NEAREST occurrence), this asks a single
    /// yes/no question about ONE fixed position -- the probe computes G/F from the Kills hit's
    /// own geometry, so there is nothing to search.</summary>
    private static bool MatchesExactlyAt(byte[] buf, int pos, byte[] pattern)
    {
        if (pattern.Length == 0) return false;
        if (pos < 0 || pos + pattern.Length > buf.Length) return false;
        return buf.AsSpan(pos, pattern.Length).SequenceEqual(pattern);
    }

    /// <summary>The bidirectional nearest-anchor search (KEY DESIGN CHANGE, plan v2): tries BOTH
    /// <see cref="NearestAnchorPos"/> (backward, largest pos &lt; pos) and
    /// <see cref="NearestAnchorPosForward"/> (forward, smallest pos &gt; pos) and returns whichever
    /// is closer by absolute distance. -1 only if neither direction finds a candidate within
    /// FlavorWindow. A strict generalization of backward-only: on any flavor-before layout this
    /// returns the identical position the old backward-only search did (the forward half can only
    /// ever narrow the answer, never override a legitimately-nearer backward hit).</summary>
    private static int NearestAnchorPosBidir(byte[] buf, int pos, IReadOnlyList<byte[]> candidates)
    {
        int before = NearestAnchorPos(buf, pos, candidates);
        int after = NearestAnchorPosForward(buf, pos, candidates);
        if (before < 0) return after;
        if (after < 0) return before;
        return (after - pos) < (pos - before) ? after : before;
    }
}
