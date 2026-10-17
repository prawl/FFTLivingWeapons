using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The verified paint-site cache. Anchor patterns (name/flavor) are re-verified at paint time to detect buffer reuse.
/// A mismatching anchor evicts on first occurrence; an unreadable one evicts only after Tuning.CardEvictStrikes consecutive misses (CardSites.Verify.cs ApplyStrike), so one transient read failure no longer deletes a live site. Skip-if-equal is not an eviction trigger.
/// Cap relief. When Add finds the kills cache at cap it re-verifies every anchor without painting, evicts dead sites and retries, at most once per PruneEveryRefusals refusals (the first cap hit after a prune or Clear prunes at once). The prune skips the strike leniency (see PruneDeadSites).
/// LW-262 moved Add/PruneDeadSites/EvictList and their state to CardSites.Admission.cs once suffix and kills got separate caps; SyncFlavorToCurrent is in CardSites.Reliquary.cs. PaintSiteWithResult stays here as one state machine whose branches each tag a PaintOutcome for CardVerdict.
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>A paint site. Weapon id, encoding, slot address, anchor address, and
    /// whether this is a kills site (true) or suffix site (false).</summary>
    internal readonly record struct Site(int Id, int Enc, long SlotAddr, long AnchorAddr, bool IsKills);

    // Key includes Id+AnchorAddr so buffer reuse (same slot, new weapon) is admitted.
    private readonly HashSet<(long slot, int enc, bool kills, int id, long anchor)> _keys = new();

    private readonly IGameMemory _mem;
    private readonly CardPatterns _pats;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md, decision 2): three-way anchor registry.
    // Null falls back to baked-only kills-site verify.
    private readonly EarnedAnchors? _anchors;
    private readonly List<Site> _sites = new();
    // Fired for each site PruneDeadSites (CardSites.Admission.cs) evicts. Null is a no-op.
    private readonly Action<Site>? _onPruneEvict;

    public CardSites(IGameMemory mem, CardPatterns pats, EarnedAnchors? anchors = null, Action<Site>? onPruneEvict = null)
    {
        _mem = mem; _pats = pats; _anchors = anchors; _onPruneEvict = onPruneEvict;
    }

    /// <summary>Number of sites in the cache (both kinds).</summary>
    public int Count => _sites.Count;

    /// <summary>Test accessor (DisplayMaintenanceTests): a point-in-time copy of the cached sites.</summary>
    internal List<Site> Snapshot() => new(_sites);

    /// <summary>Clears the cache and all Admission state (ResetAdmission, LW-262).</summary>
    public void Clear() { _sites.Clear(); _keys.Clear(); _strikes.Clear(); ResetAdmission(); }

    /// <summary>Paints the given sites; returns the number of writes. Delegates to the verdict overload with null.</summary>
    public int Paint(IEnumerable<Site> sites, Func<int, int> killsFor) => Paint(sites, killsFor, null);

    /// <summary>As above, recording each site's outcome into <paramref name="verdict"/>; null skips the ledger.</summary>
    public int Paint(IEnumerable<Site> sites, Func<int, int> killsFor, CardVerdict? verdict)
    {
        int w = 0;
        foreach (var s in sites) if (PaintSite(s, killsFor, verdict)) w++;
        return w;
    }

    /// <summary>Paints all cached sites, evicting those whose anchor verify fails; returns the number of writes. Delegates with a null verdict.</summary>
    public int PaintAll(Func<int, int> killsFor) => PaintAll(killsFor, null);

    /// <summary>PaintAll, recording each outcome into <paramref name="verdict"/> (null skips). Eviction follows PaintSiteWithResult's PaintResult, not the recorded PaintOutcome.</summary>
    public int PaintAll(Func<int, int> killsFor, CardVerdict? verdict)
    {
        int writes = 0;
        List<Site>? toEvict = null;
        foreach (var site in _sites)
        {
            var r = PaintSiteWithResult(site, killsFor, out var outcome, out var kills);
            // true only when this call removes the site; a strike short of the cap is evicted:false (CardVerdict.Note).
            bool evicted = r == PaintResult.Evict;
            verdict?.Note(site.Id, site.SlotAddr, outcome, ObservedFor(site.IsKills, outcome, kills), evicted);
            if (r == PaintResult.Write) writes++;
            else if (evicted) (toEvict ??= new List<Site>()).Add(site);
        }
        if (toEvict != null) EvictList(toEvict);
        return writes;
    }

    // ─── private ─────────────────────────────────────────────────────────────

    private enum PaintResult { NoWrite, Write, Evict }

    // Add, PruneDeadSites, and EvictList moved to CardSites.Admission.cs.

    /// <summary>CardVerdict's numeric field, kills sites only. AlreadyEqual is the value read; Wrote is the value handed to WriteBytes (void, no read-back, to avoid an extra guarded read).
    /// Suffix slots hold a tier word, not a count, so they report -1 like the refusal paths.</summary>
    private static int ObservedFor(bool isKills, PaintOutcome outcome, int kills) =>
        isKills && outcome is PaintOutcome.Wrote or PaintOutcome.AlreadyEqual ? kills : -1;

    /// <summary>Paints one site with eviction signalling and reports the reason via <paramref name="outcome"/>.
    /// Order is verify, flavor-sync (SyncFlavorToCurrent), then slot logic, so a skip-if-equal on the count slot cannot suppress the Reliquary repaint-through.
    /// VerifyAnchor's tri-state feeds ApplyStrike (CardSites.Verify.cs): Mismatch evicts at once, Unreadable only after Tuning.CardEvictStrikes in a row.
    /// <paramref name="kills"/> is the killsFor(s.Id) result threaded out for ObservedFor; it stays 0 on anchor-failure returns, which ObservedFor ignores.</summary>
    private PaintResult PaintSiteWithResult(Site s, Func<int, int> killsFor, out PaintOutcome outcome, out int kills)
    {
        kills = 0;
        var anchor = VerifyAnchor(s);
        if (anchor != AnchorState.Live)
        {
            outcome = anchor == AnchorState.Mismatch ? PaintOutcome.AnchorMismatch : PaintOutcome.AnchorUnreadable;
            return ApplyStrike(s, anchor) ? PaintResult.Evict : PaintResult.NoWrite;
        }
        ApplyStrike(s, AnchorState.Live);   // resets strikes; returns false

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
            // Slots(enc) is built once in the CardPatterns ctor.
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
        // Paint() never evicts (only PaintAll does), so evicted is always false here (CardVerdict.Note).
        // PaintSiteWithResult can return Evict on this path (a Mismatch evicts on first sight, LW-163), but Paint() ignores it;
        // ApplyStrike already ran, and a later PaintAll over the cached site finishes the eviction.
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
