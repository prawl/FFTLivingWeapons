using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// CardScanner's anchor candidate selection and nearest-anchor byte searches (LW-332, split from
/// CardScanner.cs for the 200-line trigger). FindNearestFlavor uses the Nearest* searches and
/// LayoutProbeOwner uses MatchesExactlyAt.
/// </summary>
internal static partial class CardScanner
{
    /// <summary>Anchor patterns for a weapon id at this encoding. EarnedAnchors' full set when
    /// wired, else the baked Flavor and Grows patterns (LW-332; either may be empty).</summary>
    private static IReadOnlyList<byte[]> AnchorCandidates(int id, int enc, CardPatterns pats, EarnedAnchors? anchors)
    {
        if (anchors != null) return anchors.AnchorsFor(id, enc);
        if (!pats.TryGet(id, enc, out var entry)) return Array.Empty<byte[]>();
        var list = new List<byte[]>(2);
        if (entry.Flavor.Length > 0) list.Add(entry.Flavor);
        if (entry.Grows.Length > 0) list.Add(entry.Grows);
        return list;
    }

    /// <summary><see cref="AnchorCandidates"/> minus Grows. Same-lane weapons bake identical
    /// Grows lines, so only these can disambiguate in FindNearestFlavor's two-key rule.</summary>
    private static IReadOnlyList<byte[]> UniqueAnchorCandidates(int id, int enc, CardPatterns pats, EarnedAnchors? anchors)
    {
        if (anchors != null) return anchors.UniqueAnchorsFor(id, enc);
        return pats.TryGet(id, enc, out var entry) && entry.Flavor.Length > 0
            ? new[] { entry.Flavor }
            : Array.Empty<byte[]>();
    }

    /// <summary>Largest position below pos of any candidate within [pos-FlavorWindow, pos), else -1.</summary>
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

    /// <summary>Smallest position above pos of any candidate within (pos, pos+FlavorWindow], else -1.
    /// Backs the equip-meter layout with the Kills line first and flavor below.</summary>
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

    /// <summary>True if buf at pos equals pattern, bounds-checked. LW-332 round 2's exact-layout
    /// probe, which computes G/F from the Kills hit's geometry and so needs no window search.</summary>
    private static bool MatchesExactlyAt(byte[] buf, int pos, byte[] pattern)
    {
        if (pattern.Length == 0) return false;
        if (pos < 0 || pos + pattern.Length > buf.Length) return false;
        return buf.AsSpan(pos, pattern.Length).SequenceEqual(pattern);
    }

    /// <summary>Whichever of the backward and forward searches is closer, else -1. On a
    /// flavor-before layout this matches the old backward-only result.</summary>
    private static int NearestAnchorPosBidir(byte[] buf, int pos, IReadOnlyList<byte[]> candidates)
    {
        int before = NearestAnchorPos(buf, pos, candidates);
        int after = NearestAnchorPosForward(buf, pos, candidates);
        if (before < 0) return after;
        if (after < 0) return before;
        return (after - pos) < (pos - before) ? after : before;
    }
}
