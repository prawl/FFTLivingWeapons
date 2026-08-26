using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// CardSites' anchor-verify half (LW-257 commit 1, the 200-line seam: CardSites.cs was already
/// at 234 lines before this arc). Converts the old bare-bool AnchorIsLive/KillsAnchorMatches pair
/// into a tri-state (<see cref="AnchorState"/>) plus the per-site strike bookkeeping that makes a
/// single transient unreadable read survivable: the bug this arc fixes is that CardSites.cs:184
/// (pre-fix) turned ANY false -- a genuine buffer-reuse mismatch or a one-tick unreadable memory
/// glitch alike -- into an eviction on the very first occurrence.
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>Live: the anchor (and, for kills sites, the "Kills: " literal) reads back exactly
    /// as expected. Mismatch: the read succeeded but the bytes are wrong -- the buffer was
    /// genuinely reused for something else, which is never transient. Unreadable: the read itself
    /// failed -- the dominant transient case (a save-load window, a heap generation boundary) this
    /// arc stops treating as equivalent to Mismatch.</summary>
    internal enum AnchorState { Live, Mismatch, Unreadable }

    /// <summary>Consecutive-Unreadable strike count per site. Absent (TryGetValue miss) means
    /// zero. Never holds a Mismatch count -- Mismatch evicts on its first occurrence (LW-163's
    /// existing contract, unweakened by this arc) and clears any strikes the site had accrued, so
    /// there is never a reason to remember a Mismatch count.</summary>
    private readonly Dictionary<Site, int> _strikes = new();

    /// <summary>True when the site's anchor bytes (and kills literal for kills sites) are
    /// readable and match the expected pattern. Shared by PruneDeadSites and
    /// PaintSiteWithResult so there is no duplicated verify logic. Suffix sites verify against
    /// the weapon NAME only (unchanged by Reliquary). Kills sites verify against ANY registered
    /// anchor for (id, enc) -- baked, or (when Reliquary's EarnedAnchors is wired) the current
    /// and previous earned lines too (decision 2): a site holding a stale-but-known line is
    /// live, never evicted, so its next paint can repaint it forward instead of freezing it.
    ///
    /// Read pattern is UNCHANGED from the pre-tri-state code (LW-257 commit 1 adds zero new
    /// _mem calls, per the spec's cost budget): the kills-site flavor half is checked FIRST and
    /// this method returns immediately on anything but Live from it, WITHOUT ever attempting the
    /// "Kills: " literal read.
    ///
    /// THIS IS A DELIBERATE DEVIATION, not a neutral optimization: a readable-but-WRONG flavor
    /// anchor (Mismatch) evicts immediately even though the never-attempted "Kills: " literal read
    /// might have come back Unreadable. A readable-but-wrong anchor is strong positive evidence
    /// the buffer was genuinely reused for something else -- exactly the case LW-163's contract
    /// says should evict on the spot -- so spending a second read only to learn the other half
    /// MIGHT have been unreadable would just delay a correct eviction by up to
    /// Tuning.CardEvictStrikes maintenance beats for no benefit. Mismatch wins over a hypothetical
    /// Unreadable on the untried second half; see CardSitesVerifyTests.cs's
    /// A_readable_but_wrong_flavor_evicts_even_though_the_kills_literal_would_have_been_unreadable
    /// for the pinned mixed case.</summary>
    private AnchorState VerifyAnchor(Site s)
    {
        if (!_pats.TryGet(s.Id, s.Enc, out var pat)) return AnchorState.Mismatch;

        if (!s.IsKills)
        {
            byte[] ab = pat.Name;
            if (ab.Length == 0) return AnchorState.Mismatch;
            if (!_mem.TryReadBytes(s.AnchorAddr, ab.Length, out var cur)) return AnchorState.Unreadable;
            return ByteEq(cur, ab) ? AnchorState.Live : AnchorState.Mismatch;
        }

        var flavor = VerifyKillsFlavor(s, pat);
        if (flavor != AnchorState.Live) return flavor;

        byte[] kl = _pats.Kills(s.Enc);
        long ka = s.SlotAddr - kl.Length;
        if (!_mem.TryReadBytes(ka, kl.Length, out var ck)) return AnchorState.Unreadable;
        return ByteEq(ck, kl) ? AnchorState.Live : AnchorState.Mismatch;
    }

    /// <summary>Kills-site flavor-anchor tri-state: baked Flavor + Grows (LW-332) when _anchors
    /// is null (the production path today -- Engine.cs wires legends: null); otherwise ANY of
    /// EarnedAnchors.AnchorsFor's set (baked, current, previous, Grows). Delegates to <see
    /// cref="MatchAnyAnchor"/>. Mirrors the original bool KillsAnchorMatches this replaces,
    /// read-for-read wherever every candidate still shares one length.</summary>
    private AnchorState VerifyKillsFlavor(Site s, CardPatterns.Entry pat)
    {
        if (_anchors == null)
        {
            var baked = new List<byte[]>(2);
            if (pat.Flavor.Length > 0) baked.Add(pat.Flavor);
            if (pat.Grows.Length > 0) baked.Add(pat.Grows);
            return MatchAnyAnchor(s, baked);
        }

        return MatchAnyAnchor(s, _anchors.AnchorsFor(s.Id, s.Enc));
    }

    /// <summary>Match the bytes at s.AnchorAddr against ANY of the given candidates, reading once
    /// per DISTINCT candidate byte length rather than assuming a single shared length. Before
    /// LW-332, every candidate set the anchors!=null branch saw genuinely shared one length --
    /// EarnedAnchors enforces current/previous equal-length to baked Flavor by construction --
    /// so a single read at candidates[0].Length always sufficed. LW-332 appended the baked Grows
    /// pattern to that same set (and to the null-anchors fallback) with its OWN, generally
    /// DIFFERENT, length: since FlavorPos can now resolve to the Grows line's position
    /// (Display.cs:335 via CardScanner's bidirectional search), a site's AnchorAddr may hold
    /// EITHER length, so both must be tried. In practice this is still exactly ONE read for
    /// every pre-Grows fixture (every candidate shares a length) and at most two once Grows is
    /// in play. Live on the first byte match; Unreadable only when EVERY distinct length failed
    /// to read; Mismatch otherwise (including the empty-candidates case).</summary>
    private AnchorState MatchAnyAnchor(Site s, IReadOnlyList<byte[]> candidates)
    {
        if (candidates.Count == 0) return AnchorState.Mismatch;
        bool readSomething = false;
        var triedLengths = new List<int>(2);
        foreach (var cand in candidates)
        {
            if (cand.Length == 0 || triedLengths.Contains(cand.Length)) continue;
            triedLengths.Add(cand.Length);

            if (!_mem.TryReadBytes(s.AnchorAddr, cand.Length, out var curBytes)) continue;
            readSomething = true;
            foreach (var c2 in candidates)
                if (c2.Length == curBytes.Length && ByteEq(curBytes, c2)) return AnchorState.Live;
        }
        return readSomething ? AnchorState.Mismatch : AnchorState.Unreadable;
    }

    /// <summary>The leniency policy (LW-257, load-bearing): decides whether THIS pass should
    /// evict the site, and maintains <see cref="_strikes"/> accordingly. Live resets the count --
    /// a successful verify proves the site is fine right now, so a later blip starts counting
    /// from zero rather than wherever an earlier, unrelated blip left off (a non-resetting counter
    /// is a slow leak that reaches the cap over minutes even though every individual read mostly
    /// succeeds). Mismatch evicts on the very first occurrence and clears any strikes -- LW-163's
    /// existing contract, unweakened: a genuinely reused buffer is never a transient condition, so
    /// there is nothing to wait out. Unreadable increments and only asks for eviction once the
    /// count reaches <see cref="Tuning.CardEvictStrikes"/> (3) -- see that constant's own doc for
    /// the tape this threshold is tuned from.</summary>
    private bool ApplyStrike(Site s, AnchorState state)
    {
        switch (state)
        {
            case AnchorState.Live:
                _strikes.Remove(s);
                return false;
            case AnchorState.Mismatch:
                _strikes.Remove(s);
                return true;
            default: // Unreadable
                int n = _strikes.TryGetValue(s, out int cur) ? cur + 1 : 1;
                _strikes[s] = n;
                return n >= Tuning.CardEvictStrikes;
        }
    }
}
