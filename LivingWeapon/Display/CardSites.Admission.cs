using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-262: cache admission and its accounting -- split out of CardSites.cs (which sat at 282
/// lines, over the 200-line trigger) once kills and suffix sites needed independent caps rather
/// than the one shared MaxSites both used to compete for. This IS the real seam: everything here
/// decides whether a site gets INTO the cache and what state that decision updates; everything
/// left in CardSites.cs decides what happens to a site already admitted (verify/paint/evict).
///
/// THE BUG THIS FIXES: live tape flight_20260818_011921_battle-exit.jsonl (coordinator-relayed
/// owner tape, not yet a tracked repo artifact) showed sites pinned at exactly 2048 three times
/// in one battle, kills sites at only 726-728 of those 2048 each time, one region
/// (0x15F800000) holding 1076 sites, and another (0x4F9B490000) collapsing from 242 to 0. Suffix
/// sites (121 tracked ids x 2 encodings, unbounded copies per id before this fix -- Display.
/// OnChunk's allSuffixes pass on the pool path, LW-59) were crowding out kills sites in the one
/// shared 2048-slot cache. CoversAllMeta() (Display.PoolPaint.cs) counts ONLY kills sites, so a
/// cache saturated with suffix copies could never let coverage latch, which sent the mod back
/// into the nine-second whole-process search LW-261 already fixed the COST of but not the
/// FREQUENCY of -- an endless re-search loop, not a one-off.
///
/// THE FIX: partition by kind. MaxSites (2048, unchanged) still bounds kills sites exactly as
/// before -- Add's kills branch, PruneDeadSites, and the rate-limited cap-relief prune are
/// byte-identical in behavior to pre-LW-262 for a kills-only Add sequence (CardSitesCapReliefTests
/// T1/T2/T3 stay green unmodified). Suffix sites get their OWN, independent, much smaller ceiling
/// (MaxSuffixSites) plus a per-id ceiling (SuffixCopiesPerId, both encodings pooled): a suffix
/// flood can push the suffix count up to MaxSuffixSites and no further, regardless of how many
/// kills sites exist, and regardless of how many pool-region copies of one weapon's suffix text
/// the process happens to hold. No live-site eviction was added anywhere to make room -- the two
/// caps are pure ADMISSION gates, refusing new sites rather than making room for them.
///
/// PRUNE AMPLIFICATION BOUND: a suffix policy refusal (over either cap) is a CHEAP, expected,
/// steady-state event once the suffix cap is reached (every subsequent suffix Add for a tracked
/// id refuses, forever, until something is evicted elsewhere) -- so it must never itself trigger
/// PruneDeadSites (an O(sites) anchor-reverify walk) or touch _refusalsAtCap (which exists only to
/// rate-limit the KILLS cap-relief prune). Doing either would turn a cheap, common refusal into an
/// expensive one on every single suffix Add past the cap. Symmetrically, PruneRearmFloor stops a
/// low-yield KILLS prune (one that frees only a handful of dead sites) from re-arming
/// _pruneImmediately: without the floor, a prune that evicts say 3 sites out of 2048 would still
/// flip _pruneImmediately back to true, and the very next refused Add would trigger ANOTHER full
/// O(sites) prune for another handful of sites -- a self-amplifying loop of expensive prunes that
/// each only shave a few sites off. (Verifier F7 correction: an earlier version of this doc cited
/// LW-268 here, which is the wrong ticket -- LW-268 is about the O(memory) whole-process rescan
/// the resumable search re-walks, a different cost entirely from this O(sites) in-cache prune
/// pass. No existing backlog row names this specific prune-amplification shape; none is cited.)
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>Upper bound on simultaneously-live KILLS sites. Unchanged by LW-262 (this cap
    /// governed BOTH kinds together before; now it governs kills alone, and kills genuinely may
    /// use the whole 2048 regardless of how many suffix sites are also cached -- LW-262's own
    /// class doc). LW-59's original sizing note (a ~5.8-copies-per-id estimate against a smaller
    /// sample) is retired: it was never re-measured and the 2026-08-18 tape above falsifies the
    /// premise it was built on (peak observed kills sites was 726-728 of the 2048 ceiling, not
    /// anywhere near a clean per-id multiple), so this constant is now sized to the newer,
    /// harder measurement instead of the old estimate: 2048 already clears the tape's own 726-728
    /// kills-site peak with headroom, and nothing in that tape suggests raising it further.</summary>
    internal const int MaxSites = 2048;

    /// <summary>Upper bound on simultaneously-live SUFFIX sites, INDEPENDENT of MaxSites (LW-262,
    /// verifier F2: the two are separate ceilings on a shared List, not a shared pool split
    /// between them -- kills genuinely may reach its own full 2048 at the SAME time suffix
    /// reaches its own full 1024, so the cache can legitimately hold up to 2048 + 1024 = 3072
    /// sites total; there is no "leftover headroom" arithmetic between the two constants at all).
    /// Sized well above what one weapon's suffix text needs (121 tracked ids x SuffixCopiesPerId
    /// would be 1452 if every id maxed out its own per-id cap at once, which never happens in
    /// practice -- only a handful of ids are ever actively targeted/rotated at once, Display.
    /// SuffixRotation's own per-chunk slice), while staying small enough that a suffix flood can
    /// never come remotely close to the SEPARATE 2048 a kills flood is entitled to.</summary>
    internal const int MaxSuffixSites = 1024;

    /// <summary>Per-id cap on simultaneously-live suffix sites, BOTH encodings pooled together
    /// (one weapon's 1-byte and 2-byte suffix copies share this one ceiling, not 12 each) -- a
    /// single weapon's suffix text should never need anywhere near this many live pool copies at
    /// once; this exists to stop one runaway id from alone consuming a meaningful fraction of
    /// MaxSuffixSites and starving every OTHER tracked id's own suffix coverage the same way the
    /// original bug starved kills coverage.</summary>
    internal const int SuffixCopiesPerId = 12;

    /// <summary>Minimum refused Adds between successive prune passes while the KILLS cache is
    /// saturated.</summary>
    internal const int PruneEveryRefusals = 32;

    /// <summary>Minimum sites a prune must actually evict to re-arm _pruneImmediately (this file's
    /// class doc, "PRUNE AMPLIFICATION BOUND"). A prune that frees fewer than this many sites
    /// still frees them (EvictList runs regardless), it just does not buy the NEXT refused Add an
    /// immediate re-prune -- that next refusal instead waits out the ordinary PruneEveryRefusals
    /// rate limit like any other refusal would.</summary>
    internal const int PruneRearmFloor = 16;

    // Prune-rate-limit state (kills cap only).
    private int _refusalsAtCap;
    private bool _pruneImmediately = true; // true after Clear or a prune that met PruneRearmFloor

    // LW-262: per-kind live counts, maintained incrementally (Add/EvictList) rather than scanned,
    // since Add runs on the hot discovery path (Display.OnChunk, up to a few hundred times per
    // Tick) and a per-Add O(sites) scan would undo exactly the cost discipline LW-261 fixed.
    private int _killsCount;
    private int _suffixCount;
    // Per-id suffix count, both encodings pooled (SuffixCopiesPerId's own doc). Absent key means
    // zero -- an id is only inserted once its first suffix site is admitted, removed again once
    // its count drops back to zero (EvictList), so this dictionary never grows unbounded across a
    // tally reset the way an id-keyed structure with no removal path would.
    private readonly Dictionary<int, int> _suffixCountById = new();

    /// <summary>Test accessor (CardSitesSuffixPartitionTests): live KILLS sites currently cached.</summary>
    internal int KillsCount => _killsCount;

    /// <summary>Test accessor and Display.PoolPaint.cs's coverage-payload source: live SUFFIX
    /// sites currently cached.</summary>
    internal int SuffixCount => _suffixCount;

    /// <summary>Test accessor (CardSitesSuffixPartitionTests.Policy_refused_suffix_add_never_
    /// triggers_prune): proves a suffix policy refusal left the kills-prune rate-limit state
    /// completely untouched.</summary>
    internal int RefusalsAtCapForTest => _refusalsAtCap;

    /// <summary>Clear()'s second half (CardSites.cs): resets every piece of Admission state,
    /// including the two new per-kind counters and the per-id dictionary.</summary>
    private void ResetAdmission()
    {
        _refusalsAtCap = 0;
        _pruneImmediately = true;
        _killsCount = 0;
        _suffixCount = 0;
        _suffixCountById.Clear();
    }

    /// <summary>Add a site if not already present (dedup by SlotAddr/Enc/IsKills/Id/AnchorAddr).
    /// Kills sites (IsKills true) are gated by MaxSites alone, with the pre-existing rate-limited
    /// cap-relief prune (this file's class doc) -- byte-identical to pre-LW-262 behavior for an
    /// all-kills Add sequence. Suffix sites (IsKills false) are gated by MaxSuffixSites and
    /// SuffixCopiesPerId instead, and a refusal on either NEVER prunes and NEVER touches
    /// _refusalsAtCap (the "prune amplification bound" this file's class doc explains) -- a
    /// suffix-cap refusal is meant to be cheap and common in steady state, not a trigger for an
    /// O(sites) anchor-reverify walk. Returns true if added, false if duplicate or a cap refused
    /// admission.</summary>
    public bool Add(Site s)
    {
        if (s.IsKills)
        {
            if (_killsCount >= MaxSites)
            {
                if (_pruneImmediately || _refusalsAtCap % PruneEveryRefusals == 0)
                    PruneDeadSites();
                _refusalsAtCap++;
                if (_killsCount >= MaxSites)
                    return false;
            }
        }
        else
        {
            if (_suffixCount >= MaxSuffixSites) return false;
            _suffixCountById.TryGetValue(s.Id, out int idCount);
            if (idCount >= SuffixCopiesPerId) return false;
        }

        var key = (s.SlotAddr, s.Enc, s.IsKills, s.Id, s.AnchorAddr);
        if (!_keys.Add(key)) return false;
        _sites.Add(s);
        if (s.IsKills) _killsCount++;
        else
        {
            _suffixCount++;
            _suffixCountById[s.Id] = _suffixCountById.TryGetValue(s.Id, out int c) ? c + 1 : 1;
        }
        return true;
    }

    /// <summary>Scan all cached sites (both kinds -- a dead site is dead regardless of kind, and
    /// freeing a dead SUFFIX site is exactly as valid a prune outcome as freeing a dead kills one)
    /// and evict those with a dead anchor. Always runs when called (Add's kills branch is the only
    /// caller); PruneRearmFloor governs only whether the NEXT refusal gets to skip the
    /// PruneEveryRefusals rate limit, never whether THIS prune itself runs or evicts.
    ///
    /// Deliberately bypasses the strike leniency (LW-257): this is a cap-relief pass, called only
    /// when Add finds the KILLS cache genuinely full. Its whole job is to free room for a new site
    /// RIGHT NOW, so it must evict everything not currently verifiable in one pass -- waiting out
    /// a possibly-transient Unreadable site here would let Add keep failing while genuinely dead
    /// entries sit on strike 1 or 2. PaintAll's own maintenance passes are a different question
    /// (steady-state hygiene, not "we are stuck at cap"), which is exactly where the leniency
    /// belongs instead.
    ///
    /// LW-262: every site this pass evicts now reaches _onPruneEvict (CardSites.cs's ctor param;
    /// null is a no-op, the CardVerdict-null idiom) -- the round-3 review's "UNTAPPED" gap this
    /// doc used to describe is closed. Display.Flight.cs wires it to a "card" flight record
    /// reusing the EXISTING _flightBudget tiers (not a new reserve): semantically this is the same
    /// "site-evicted" event EmitVerdict already emits for the strike-driven eviction path, just
    /// sourced from the cap-relief path instead.</summary>
    private void PruneDeadSites()
    {
        List<Site>? toEvict = null;
        foreach (var s in _sites) if (VerifyAnchor(s) != AnchorState.Live) (toEvict ??= new List<Site>()).Add(s);
        if (toEvict != null)
        {
            EvictList(toEvict);
            foreach (var s in toEvict) _onPruneEvict?.Invoke(s);
            if (toEvict.Count >= PruneRearmFloor) { _refusalsAtCap = 0; _pruneImmediately = true; }
            else _pruneImmediately = false;   // freed something, but not enough to re-arm (class doc)
        }
        else _pruneImmediately = false;
    }

    /// <summary>Remove a list of sites from _sites, _keys, _strikes (LW-257: an evicted site must
    /// not leave a stale strike count behind for some future, unrelated site that happens to reuse
    /// the same Site key shape -- unbounded growth guard, per CardSites.Verify.cs's own class
    /// doc), and the LW-262 per-kind counters (KillsCount/SuffixCount/_suffixCountById), so
    /// capacity freed by EITHER eviction path (this prune pass, or PaintAll's strike-driven evict)
    /// is correctly returned to a subsequent Add of either kind.</summary>
    private void EvictList(List<Site> list)
    {
        foreach (var s in list)
        {
            _sites.Remove(s);
            _keys.Remove((s.SlotAddr, s.Enc, s.IsKills, s.Id, s.AnchorAddr));
            _strikes.Remove(s);
            if (s.IsKills) _killsCount--;
            else
            {
                _suffixCount--;
                if (_suffixCountById.TryGetValue(s.Id, out int c))
                {
                    if (c <= 1) _suffixCountById.Remove(s.Id);
                    else _suffixCountById[s.Id] = c - 1;
                }
            }
        }
    }
}
