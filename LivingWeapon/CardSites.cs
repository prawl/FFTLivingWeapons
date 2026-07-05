using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The verified paint-site cache. Anchor patterns (name/flavor) are re-verified at
/// paint time to detect buffer reuse. A site whose anchor fails during PaintAll is
/// evicted. Skip-if-equal (steady state) is NOT an eviction trigger.
///
/// Cap-relief prune (F1): when Add finds the cache at cap it first runs a prune pass
/// -- re-verifying every site's anchor without painting -- to evict dead entries, then
/// retries the admit.  Rate-limited to at most one prune per PruneEveryRefusals
/// refusals while saturated; the FIRST cap-hit after any successful prune or Clear
/// always prunes immediately (so the status-card case never waits 32 cycles).
/// </summary>
internal sealed class CardSites
{
    /// <summary>A paint site: weapon id, encoding, slot address, anchor address, and
    /// whether this is a kills site (true) or suffix site (false).</summary>
    internal readonly record struct Site(int Id, int Enc, long SlotAddr, long AnchorAddr, bool IsKills);

    // Dedup key includes Id+AnchorAddr so buffer reuse (same slot, different weapon)
    // is NOT treated as a duplicate -- the new-owner site must be admitted.
    private readonly HashSet<(long slot, int enc, bool kills, int id, long anchor)> _keys = new();

    /// <summary>Upper bound on simultaneously-live cached sites.</summary>
    internal const int MaxSites = 768;

    /// <summary>Minimum refused Adds between successive prune passes while saturated.</summary>
    internal const int PruneEveryRefusals = 32;

    private readonly IGameMemory _mem;
    private readonly CardPatterns _pats;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md, decision 12): the three-way anchor registry.
    // Null for every pre-Reliquary caller/test -- kills-site verify falls back to baked-only,
    // byte-identical to the original behavior.
    private readonly EarnedAnchors? _anchors;
    private readonly List<Site> _sites = new();

    // Prune-rate-limit state.
    private int _refusalsAtCap;
    private bool _pruneImmediately = true; // true after Clear or successful prune

    public CardSites(IGameMemory mem, CardPatterns pats, EarnedAnchors? anchors = null)
    {
        _mem = mem;  _pats = pats;  _anchors = anchors;
    }

    /// <summary>Number of sites in the cache.</summary>
    public int Count => _sites.Count;

    /// <summary>Dev-probe (FlavorSpike, P4) + test accessor: a point-in-time copy of the cached sites.</summary>
    internal List<Site> Snapshot() => new(_sites);

    /// <summary>Clear the cache and reset the prune rate-limit.</summary>
    public void Clear() { _sites.Clear(); _keys.Clear(); _refusalsAtCap = 0; _pruneImmediately = true; }

    /// <summary>Add a site if not already present (dedup by SlotAddr/Enc/IsKills/Id/AnchorAddr).
    /// When at cap, attempts a prune pass first (rate-limited).
    /// Returns true if added, false if duplicate or cap cannot be relieved.</summary>
    public bool Add(Site s)
    {
        if (_sites.Count >= MaxSites)
        {
            if (_pruneImmediately || _refusalsAtCap % PruneEveryRefusals == 0)
                PruneDeadSites();
            _refusalsAtCap++;
            if (_sites.Count >= MaxSites)
                return false;
        }
        var key = (s.SlotAddr, s.Enc, s.IsKills, s.Id, s.AnchorAddr);
        if (!_keys.Add(key)) return false;
        _sites.Add(s);
        return true;
    }

    /// <summary>Paint the given sites. Returns the number of writes issued.</summary>
    public int Paint(IEnumerable<Site> sites, Func<int, int> killsFor)
    {
        int w = 0;
        foreach (var s in sites) if (PaintSite(s, killsFor)) w++;
        return w;
    }

    /// <summary>Paint all cached sites, evicting those whose anchor verify fails.
    /// Returns the number of writes issued.</summary>
    public int PaintAll(Func<int, int> killsFor)
    {
        int writes = 0;
        List<Site>? toEvict = null;
        foreach (var site in _sites)
        {
            var r = PaintSiteWithResult(site, killsFor);
            if (r == PaintResult.Write)  writes++;
            else if (r == PaintResult.Evict) (toEvict ??= new List<Site>()).Add(site);
        }
        if (toEvict != null) EvictList(toEvict);
        return writes;
    }

    // ─── private ─────────────────────────────────────────────────────────────

    private enum PaintResult { NoWrite, Write, Evict }

    /// <summary>Scan all cached sites and evict those with a dead anchor.  Resets the
    /// prune rate-limit on any eviction; marks _pruneImmediately=false otherwise so the
    /// rate-limit keeps ticking when no sites could be freed.</summary>
    private void PruneDeadSites()
    {
        List<Site>? toEvict = null;
        foreach (var s in _sites) if (!AnchorIsLive(s)) (toEvict ??= new List<Site>()).Add(s);
        if (toEvict != null) { EvictList(toEvict); _refusalsAtCap = 0; _pruneImmediately = true; }
        else _pruneImmediately = false;
    }

    /// <summary>Remove a list of sites from both _sites and _keys.</summary>
    private void EvictList(List<Site> list)
    {
        foreach (var s in list)
        {
            _sites.Remove(s);
            _keys.Remove((s.SlotAddr, s.Enc, s.IsKills, s.Id, s.AnchorAddr));
        }
    }

    /// <summary>True when the site's anchor bytes (and kills literal for kills sites) are
    /// readable and match the expected pattern.  Shared by PruneDeadSites and
    /// PaintSiteWithResult so there is no duplicated verify logic. Suffix sites verify against
    /// the weapon NAME only (unchanged by Reliquary). Kills sites verify against ANY registered
    /// anchor for (id, enc) -- baked, or (when Reliquary's EarnedAnchors is wired) the current
    /// and previous earned lines too (decision 12): a site holding a stale-but-known line is
    /// live, never evicted, so its next paint can repaint it forward instead of freezing it.</summary>
    private bool AnchorIsLive(Site s)
    {
        if (!_pats.TryGet(s.Id, s.Enc, out var pat)) return false;

        if (!s.IsKills)
        {
            byte[] ab = pat.Name;
            if (ab.Length == 0) return false;
            return _mem.TryReadBytes(s.AnchorAddr, ab.Length, out var cur) && ByteEq(cur, ab);
        }

        if (!KillsAnchorMatches(s, pat)) return false;

        byte[] kl = _pats.Kills(s.Enc);
        long ka = s.SlotAddr - kl.Length;
        if (!_mem.TryReadBytes(ka, kl.Length, out var ck)) return false;
        return ByteEq(ck, kl);
    }

    /// <summary>Kills-site anchor check: baked-only when _anchors is null (every pre-Reliquary
    /// caller); otherwise ANY of [baked, current, previous] (EarnedAnchors.AnchorsFor -- every
    /// candidate is enforced equal-length by construction, so one read suffices).</summary>
    private bool KillsAnchorMatches(Site s, CardPatterns.Entry pat)
    {
        if (_anchors == null)
        {
            byte[] ab = pat.Flavor;
            if (ab.Length == 0) return false;
            return _mem.TryReadBytes(s.AnchorAddr, ab.Length, out var cur) && ByteEq(cur, ab);
        }

        var candidates = _anchors.AnchorsFor(s.Id, s.Enc);
        if (candidates.Count == 0) return false;
        if (!_mem.TryReadBytes(s.AnchorAddr, candidates[0].Length, out var curBytes)) return false;
        foreach (var cand in candidates)
            if (cand.Length == curBytes.Length && ByteEq(curBytes, cand)) return true;
        return false;
    }

    /// <summary>Paint a single site with eviction signalling. Order is verify -> flavor-sync ->
    /// slot logic (review pin): the Reliquary repaint-through (SyncFlavorToCurrent) runs right
    /// after a successful verify, BEFORE any of the slot-write early returns below, so a
    /// skip-if-equal or invalid-digit NoWrite on the count slot can never suppress it.</summary>
    private PaintResult PaintSiteWithResult(Site s, Func<int, int> killsFor)
    {
        if (!AnchorIsLive(s)) return PaintResult.Evict;

        if (s.IsKills && _anchors != null) SyncFlavorToCurrent(s);

        int kills = killsFor(s.Id);
        byte[] desired = s.IsKills
            ? ByteScan.Enc(Signatures.KillsSlot(kills), s.Enc)
            : ByteScan.Enc(Tuning.Suffix[Tuning.TierFor(kills)], s.Enc);

        if (!_mem.TryReadBytes(s.SlotAddr, desired.Length, out var cur))
            return PaintResult.NoWrite;

        if (s.IsKills) { if (!ByteScan.KillsDigits(cur, 0, s.Enc)) return PaintResult.NoWrite; }
        else
        {
            // Slots(enc) returns the list CardPatterns built once at ctor -- no copy per call.
            if (!ByteScan.MatchesAny(cur, 0, _pats.Slots(s.Enc), desired.Length)) return PaintResult.NoWrite;
        }

        if (ByteEq(cur, desired)) return PaintResult.NoWrite; // skip-if-equal
        if (!_mem.Writable(s.SlotAddr, desired.Length)) return PaintResult.NoWrite;
        _mem.WriteBytes(s.SlotAddr, desired);
        return PaintResult.Write;
    }

    /// <summary>Reliquary decision 12's repaint-through: if a CURRENT earned line is registered
    /// for this site's weapon and the on-screen anchor bytes don't already match it, overwrite
    /// them (exact length, Writable-gated, skip-if-equal). This is what lets a site that verified
    /// via the PREVIOUS anchor (a stale-but-known line) converge to the current story on its very
    /// next paint, instead of freezing at whatever text it happened to verify against. No store
    /// writes here -- painting never touches LegendStore.</summary>
    private void SyncFlavorToCurrent(Site s)
    {
        byte[]? current = _anchors!.CurrentFor(s.Id, s.Enc);
        if (current == null || current.Length == 0) return;
        if (!_mem.TryReadBytes(s.AnchorAddr, current.Length, out var cur)) return;
        if (ByteEq(cur, current)) return;   // skip-if-equal -- already showing the current line
        if (!_mem.Writable(s.AnchorAddr, current.Length)) return;
        _mem.WriteBytes(s.AnchorAddr, current);
        ModLogger.LogDebug($"card-sites: repainted weapon {s.Id} enc {s.Enc}'s story line to the current composed text");
    }

    private bool PaintSite(Site s, Func<int, int> killsFor) => PaintSiteWithResult(s, killsFor) == PaintResult.Write;

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
