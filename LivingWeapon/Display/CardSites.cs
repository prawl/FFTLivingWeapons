using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The verified paint-site cache. Anchor patterns (name/flavor) are re-verified at paint time to
/// detect buffer reuse. LW-257: a MISMATCHING anchor evicts on its first occurrence, same as
/// before; an UNREADABLE anchor (a transient read failure, the dominant real-world case) is
/// leniently retried and evicts only after Tuning.CardEvictStrikes consecutive misses
/// (CardSites.Verify.cs's ApplyStrike) -- the two used to be indistinguishable, which is the bug
/// this arc fixes: a single unreadable frame used to delete a live, correctly-painted site.
/// Skip-if-equal (steady state) is NOT an eviction trigger, before or after.
///
/// Cap-relief prune (F1): when Add finds the cache at cap it first runs a prune pass -- re-
/// verifying every site's anchor without painting -- to evict dead entries, then retries the
/// admit. Rate-limited to at most one prune per PruneEveryRefusals refusals while saturated; the
/// FIRST cap-hit after any successful prune or Clear always prunes immediately (so the
/// status-card case never waits 32 cycles). Deliberately EXEMPT from the strike leniency above
/// (PruneDeadSites's own doc): a cap-relief pass needs room back now, not after a strike window.
///
/// LINE COUNT: this file sits at 282 (checked, not estimated -- round-4 review caught an earlier
/// "~261" claim here that was already stale by the time it was written), over both the 200-line
/// trigger and its own 234-line pre-LW-257 size. SyncFlavorToCurrent already moved out
/// (CardSites.Reliquary.cs, a real seam); what remains is one state machine (PaintSiteWithResult)
/// whose seven branches each now tag their own PaintOutcome for CardVerdict, which is exactly the
/// fidelity this arc exists to add -- splitting that method across files would be the
/// state-machine fragmentation the house rules warn against, not a seam. Going over is the honest
/// call here, not a further split.
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>A paint site: weapon id, encoding, slot address, anchor address, and
    /// whether this is a kills site (true) or suffix site (false).</summary>
    internal readonly record struct Site(int Id, int Enc, long SlotAddr, long AnchorAddr, bool IsKills);

    // Dedup key includes Id+AnchorAddr so buffer reuse (same slot, different weapon)
    // is NOT treated as a duplicate -- the new-owner site must be admitted.
    private readonly HashSet<(long slot, int enc, bool kills, int id, long anchor)> _keys = new();

    /// <summary>Upper bound on simultaneously-live cached sites. LW-59: raised from 768 so the
    /// pool path's all-ids suffix pass (Display.OnChunk's allSuffixes gate) is never refused at
    /// the cap. Sizing: ~701 live kills sites observed on the 2026-07-10 tape
    /// (docs/CHANGELOG.md LW-37 row) across 121 tracked ids is about 5.8 pool copies per id;
    /// 121 ids x 2 site kinds (kills + suffix) x 8-copy headroom (rounded up from that 5.8)
    /// = 1936, which 2048 clears.</summary>
    internal const int MaxSites = 2048;

    /// <summary>Minimum refused Adds between successive prune passes while saturated.</summary>
    internal const int PruneEveryRefusals = 32;

    private readonly IGameMemory _mem;
    private readonly CardPatterns _pats;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md, decision 2): the three-way anchor registry.
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

    /// <summary>Test accessor (DisplayMaintenanceTests): a point-in-time copy of the cached sites.</summary>
    internal List<Site> Snapshot() => new(_sites);

    /// <summary>Clear the cache and reset the prune rate-limit.</summary>
    public void Clear() { _sites.Clear(); _keys.Clear(); _strikes.Clear(); _refusalsAtCap = 0; _pruneImmediately = true; }

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

    /// <summary>Paint the given sites. Returns the number of writes issued. Byte-identical
    /// signature preserved (LW-257): delegates to the verdict-taking overload with null, which
    /// is a pure no-op ledger skip, so behavior is unchanged for every existing caller/test.</summary>
    public int Paint(IEnumerable<Site> sites, Func<int, int> killsFor) => Paint(sites, killsFor, null);

    /// <summary>Paint the given sites, additionally recording each one's outcome into
    /// <paramref name="verdict"/> (null skips the ledger entirely -- no behavior change,
    /// LW-257 commit 1).</summary>
    public int Paint(IEnumerable<Site> sites, Func<int, int> killsFor, CardVerdict? verdict)
    {
        int w = 0;
        foreach (var s in sites) if (PaintSite(s, killsFor, verdict)) w++;
        return w;
    }

    /// <summary>Paint all cached sites, evicting those whose anchor verify fails. Returns the
    /// number of writes issued. Byte-identical signature preserved (LW-257): delegates to the
    /// verdict-taking overload with null.</summary>
    public int PaintAll(Func<int, int> killsFor) => PaintAll(killsFor, null);

    /// <summary>PaintAll, additionally recording each site's outcome into <paramref
    /// name="verdict"/> (null skips the ledger entirely -- no behavior change, LW-257 commit 1).
    /// Eviction itself is unchanged: still driven by PaintSiteWithResult's PaintResult, not by
    /// the recorded PaintOutcome (the two are related but not the same axis -- see
    /// PaintSiteWithResult's own doc).</summary>
    public int PaintAll(Func<int, int> killsFor, CardVerdict? verdict)
    {
        int writes = 0;
        List<Site>? toEvict = null;
        foreach (var site in _sites)
        {
            var r = PaintSiteWithResult(site, killsFor, out var outcome, out var kills);
            // true only when THIS call removes the site (below); a strike short of the cap is
            // evicted:false despite an AnchorUnreadable outcome (CardVerdict.Note's own doc).
            bool evicted = r == PaintResult.Evict;
            verdict?.Note(site.Id, site.SlotAddr, outcome, ObservedFor(site.IsKills, outcome, kills), evicted);
            if (r == PaintResult.Write)  writes++;
            else if (evicted) (toEvict ??= new List<Site>()).Add(site);
        }
        if (toEvict != null) EvictList(toEvict);
        return writes;
    }

    // ─── private ─────────────────────────────────────────────────────────────

    private enum PaintResult { NoWrite, Write, Evict }

    /// <summary>Scan all cached sites and evict those with a dead anchor. Resets the
    /// prune rate-limit on any eviction; marks _pruneImmediately=false otherwise so the
    /// rate-limit keeps ticking when no sites could be freed.
    ///
    /// Deliberately bypasses the strike leniency (LW-257): this is a cap-relief pass, called
    /// only when Add finds the cache genuinely full (CardSites.cs's class doc, "F1"). Its whole
    /// job is to free room for a new site RIGHT NOW, so it must evict everything not currently
    /// verifiable in one pass -- waiting out a possibly-transient Unreadable site here would let
    /// Add keep failing while genuinely dead entries sit on strike 1 or 2. PaintAll's own
    /// maintenance passes are a different question (steady-state hygiene, not "we are stuck at
    /// cap"), which is exactly where the leniency belongs instead.
    ///
    /// UNTAPPED (round-3 review, F5): this path evicts through EvictList with no CardVerdict in
    /// scope at all -- Add's own call chain never threads one through, and adding it here would be
    /// more invasion (a verdict parameter down through Add) than the diagnostic is worth for a
    /// cap-relief pass that should be rare in practice (CardSites.MaxSites is sized with headroom).
    /// A live reader: a drain sourced from HERE, not from PaintAll's own strike policy, shows up
    /// only as a falling `sites=` count in a coverage record with NO matching site-evicted lines --
    /// silence here does not mean nothing happened, it means this specific path is not wired.</summary>
    private void PruneDeadSites()
    {
        List<Site>? toEvict = null;
        foreach (var s in _sites) if (VerifyAnchor(s) != AnchorState.Live) (toEvict ??= new List<Site>()).Add(s);
        if (toEvict != null) { EvictList(toEvict); _refusalsAtCap = 0; _pruneImmediately = true; }
        else _pruneImmediately = false;
    }

    /// <summary>Remove a list of sites from _sites, _keys, AND _strikes (LW-257: an evicted site
    /// must not leave a stale strike count behind for some future, unrelated site that happens to
    /// reuse the same Site key shape -- unbounded growth guard, per CardSites.Verify.cs's own
    /// class doc).</summary>
    private void EvictList(List<Site> list)
    {
        foreach (var s in list)
        {
            _sites.Remove(s);
            _keys.Remove((s.SlotAddr, s.Enc, s.IsKills, s.Id, s.AnchorAddr));
            _strikes.Remove(s);
        }
    }

    /// <summary>CardVerdict's numeric field -- KILLS SITES ONLY. AlreadyEqual: the value the slot
    /// was READ holding, a genuine match (skip-if-equal's own comparison). Wrote: the value just
    /// HANDED to WriteBytes, not a verified read-back -- IGameMemory.WriteBytes returns void, and
    /// reading the slot back to confirm would cost a new guarded read this arc's zero-syscall
    /// budget does not spend. A suffix slot holds a tier word, never a count, so !isKills always
    /// reports -1 (the same sentinel every refusal path already uses) rather than print a lying
    /// "to=42" for a slot that never held 42.</summary>
    private static int ObservedFor(bool isKills, PaintOutcome outcome, int kills) =>
        isKills && outcome is PaintOutcome.Wrote or PaintOutcome.AlreadyEqual ? kills : -1;

    /// <summary>Paint a single site with eviction signalling, and report WHY via <paramref
    /// name="outcome"/> (LW-257: every early return used to collapse into an anonymous
    /// PaintResult.NoWrite or PaintResult.Evict; this names the specific reason without changing
    /// any of the five early-return conditions themselves). Order is verify -> flavor-sync ->
    /// slot logic (review pin): the Reliquary repaint-through (SyncFlavorToCurrent) runs right
    /// after a successful verify, BEFORE any of the slot-write early returns below, so a
    /// skip-if-equal or invalid-digit NoWrite on the count slot can never suppress it.
    ///
    /// Eviction is no longer "any anchor failure": VerifyAnchor's tri-state feeds ApplyStrike's
    /// leniency policy (CardSites.Verify.cs) -- a Mismatch still evicts on its first occurrence
    /// (LW-163's contract, unweakened), but an Unreadable read now only evicts once it has
    /// recurred Tuning.CardEvictStrikes times in a row, so a single transient miss can no longer
    /// delete a live site (the bug this arc fixes, formerly this method's own first line: `if
    /// (!AnchorIsLive(s)) return PaintResult.Evict;`).
    ///
    /// <paramref name="kills"/> (round-3 review nit): the SAME killsFor(s.Id) result every caller
    /// needs for ObservedFor, threaded out instead of re-invoked -- calling it twice per site cost
    /// nothing on the memory budget (killsFor is a plain dictionary lookup, never a guarded read)
    /// but was still a pointless duplicate call the caller can avoid for free. Left at its default
    /// (0) on every anchor-failure early return below; harmless, since ObservedFor's own outcome
    /// gate (Wrote/AlreadyEqual only) never reads it for those outcomes regardless.</summary>
    private PaintResult PaintSiteWithResult(Site s, Func<int, int> killsFor, out PaintOutcome outcome, out int kills)
    {
        kills = 0;
        var anchor = VerifyAnchor(s);
        if (anchor != AnchorState.Live)
        {
            outcome = anchor == AnchorState.Mismatch ? PaintOutcome.AnchorMismatch : PaintOutcome.AnchorUnreadable;
            return ApplyStrike(s, anchor) ? PaintResult.Evict : PaintResult.NoWrite;
        }
        ApplyStrike(s, AnchorState.Live);   // resets any accrued strikes; return value always false here

        if (s.IsKills && _anchors != null) SyncFlavorToCurrent(s);

        kills = killsFor(s.Id);
        byte[] desired = s.IsKills
            ? ByteScan.Enc(Signatures.KillsMeterSlot(kills), s.Enc)
            : ByteScan.Enc(Tuning.Suffix[Tuning.TierFor(kills)], s.Enc);

        if (!_mem.TryReadBytes(s.SlotAddr, desired.Length, out var cur))
        {
            outcome = PaintOutcome.SlotUnreadable;
            return PaintResult.NoWrite;
        }

        if (s.IsKills)
        {
            if (!ByteScan.MeterSlotDigits(cur, 0, s.Enc, Signatures.KillsMeterSlotChars))
            {
                outcome = PaintOutcome.SlotShapeRefused;
                return PaintResult.NoWrite;
            }
        }
        else
        {
            // Slots(enc) returns the list CardPatterns built once at ctor -- no copy per call.
            if (!ByteScan.MatchesAny(cur, 0, _pats.Slots(s.Enc), desired.Length))
            {
                outcome = PaintOutcome.SlotShapeRefused;
                return PaintResult.NoWrite;
            }
        }

        if (ByteEq(cur, desired)) { outcome = PaintOutcome.AlreadyEqual; return PaintResult.NoWrite; }   // skip-if-equal
        if (!_mem.Writable(s.SlotAddr, desired.Length)) { outcome = PaintOutcome.NotWritable; return PaintResult.NoWrite; }
        _mem.WriteBytes(s.SlotAddr, desired);
        outcome = PaintOutcome.Wrote;
        return PaintResult.Write;
    }

    // SyncFlavorToCurrent (Reliquary repaint-through) moved to CardSites.Reliquary.cs.

    private bool PaintSite(Site s, Func<int, int> killsFor, CardVerdict? verdict)
    {
        var r = PaintSiteWithResult(s, killsFor, out var outcome, out var kills);
        // Paint() (OnChunk's freshly-discovered sites) never evicts -- only PaintAll does --
        // so evicted is always false here (CardVerdict.Note's own doc).
        verdict?.Note(s.Id, s.SlotAddr, outcome, ObservedFor(s.IsKills, outcome, kills), evicted: false);
        return r == PaintResult.Write;
    }

    private static bool ByteEq(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
