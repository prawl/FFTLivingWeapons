using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// CardSites anchor verification, split out of CardSites.cs for the 200-line limit.
/// Returns a tri-state (<see cref="AnchorState"/>) with per-site strikes, so one transient
/// unreadable read no longer evicts a site (CardSites.cs:184 did before the fix).
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>Live. The anchor reads back as expected. Mismatch. The read succeeded but the
    /// bytes differ (buffer reused, never transient). Unreadable. The read failed (save-load
    /// window, heap generation boundary).</summary>
    internal enum AnchorState { Live, Mismatch, Unreadable }

    /// <summary>Consecutive Unreadable count per site; absent means zero. Mismatch evicts at once
    ///  and clears the entry, so it is never stored.</summary>
    private readonly Dictionary<Site, int> _strikes = new();

    /// <summary>True when the anchor bytes (and the kills literal for kills sites) read back as
    /// expected. Suffix sites check the weapon name only. Kills sites accept any registered anchor
    /// for (id, enc), including Reliquary's current and previous earned lines (decision 2), so a
    /// stale-but-known line stays live and gets repainted forward.
    ///
    /// The flavor check runs first and returns early on anything but Live, so the "Kills: "
    /// literal is not read after a wrong flavor. A readable-but-wrong anchor means the buffer was
    /// reused (LW-163 evicts at once), and a second read would only delay that by up to
    /// Tuning.CardEvictStrikes beats. Pinned by CardSitesVerifyTests.cs
    /// A_readable_but_wrong_flavor_evicts_even_though_the_kills_literal_would_have_been_unreadable.</summary>
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

    /// <summary>Flavor-anchor state for a kills site. Baked Flavor + Grows when _anchors
    /// is null (production today, Engine.cs wires legends. Null), else everything
    /// EarnedAnchors.AnchorsFor returns. Delegates to <see cref="MatchAnyAnchor"/>.</summary>
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

    /// <summary>Match the bytes at s.AnchorAddr against any candidate, reading once per distinct
    /// candidate length. LW-332 added the baked Grows pattern, whose length differs from Flavor,
    /// and FlavorPos can now resolve to the Grows line (Display.cs:335, CardScanner's
    /// bidirectional search), so both lengths must be tried. Live on the first match; Unreadable
    /// only when every read failed; Mismatch otherwise, including an empty candidate list.</summary>
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

    /// <summary>Decides whether this pass evicts the site and updates <see cref="_strikes"/>.
    /// Live clears the count so an old blip does not accumulate. Mismatch evicts immediately and
    /// clears it. Unreadable evicts only at <see cref="Tuning.CardEvictStrikes"/> (3);
    /// see that constant for the tape the threshold comes from.</summary>
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
