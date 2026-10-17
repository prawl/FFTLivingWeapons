using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Cache admission and its accounting, split out of CardSites.cs (over the 200-line
/// trigger). Kills and suffix sites get independent caps because they used to share one 2048.
/// Tape flight_20260818_011921_battle-exit.jsonl (coordinator-relayed, not a tracked artifact)
/// showed the cache pinned at 2048 three times in one battle with only 726-728 kills sites; one
/// region (0x15F800000) held 1076 sites and another (0x4F9B490000) fell from 242 to 0. Unbounded
/// suffix copies (121 ids x 2 encodings, Display.OnChunk's allSuffixes pass) crowded out
/// kills sites, and CoversAllMeta (Display.PoolPaint.cs) counts only kills, so coverage never
/// latched and the nine-second whole-process search (resumable now, but still a cost) kept repeating.
/// Admission is the only lever. No live site is evicted to make room.
/// </summary>
internal sealed partial class CardSites
{
    /// <summary>Cap on live kills sites, independent of the suffix cap. The old
    /// per-id estimate is retired; the 2026-08-18 tape peaked at 726-728 kills sites. The extended inventory
    /// (2026-08-31) pushed it to 4096 because it adds up to 121 growing weapons
    /// (ExtendedSites.MaxExtendedCount) and CardSitesCacheTests pins ids x 2 kinds x 8 copies, so
    /// 242 ids need 3872. The list grows incrementally, so headroom costs nothing.</summary>
    internal const int MaxSites = 4096;

    /// <summary>Cap on live suffix sites, separate from MaxSites, so the cache can hold both
    /// maxima at once. 121 ids x SuffixCopiesPerId is 1452, which never happens since only a few ids
    /// rotate at a time (Display.SuffixRotation's per-chunk slice).</summary>
    internal const int MaxSuffixSites = 1024;

    /// <summary>Per-id cap on live suffix sites, both encodings pooled. Stops one runaway id from
    /// starving every other id's suffix coverage.</summary>
    internal const int SuffixCopiesPerId = 12;

    /// <summary>Minimum refused Adds between prune passes while the kills cache is full.</summary>
    internal const int PruneEveryRefusals = 32;

    /// <summary>Minimum sites a prune must evict to re-arm _pruneImmediately. PruneDeadSites is an
    /// O(sites) anchor-reverify walk, so without this floor a prune that frees a handful (say 3) of
    /// sites re-arms the flag and the very next refused Add runs another full prune, a self-amplifying
    /// loop. A smaller prune still evicts; the next refusal just waits out PruneEveryRefusals.</summary>
    internal const int PruneRearmFloor = 16;

    // Prune-rate-limit state (kills cap only).
    private int _refusalsAtCap;
    private bool _pruneImmediately = true; // true after Clear or a prune that met PruneRearmFloor

    // Per-kind live counts, kept incrementally because Add is on the hot discovery path
    // (Display.OnChunk) and an O(sites) scan per Add would undo the resumable locate's savings.
    private int _killsCount;
    private int _suffixCount;
    // Per-id suffix count, both encodings pooled. A missing key means zero; EvictList removes
    // ids that reach zero so the map cannot grow across tally resets.
    private readonly Dictionary<int, int> _suffixCountById = new();

    /// <summary>Test accessor (CardSitesSuffixPartitionTests): live kills sites currently cached.</summary>
    internal int KillsCount => _killsCount;

    /// <summary>Test accessor and Display.PoolPaint.cs's coverage-payload source. Live suffix
    /// sites currently cached.</summary>
    internal int SuffixCount => _suffixCount;

    /// <summary>Test accessor. CardSitesSuffixPartitionTests.Policy_refused_suffix_add_never_triggers_prune
    /// uses it to show a suffix refusal leaves the kills prune rate-limit state alone.</summary>
    internal int RefusalsAtCapForTest => _refusalsAtCap;

    /// <summary>Second half of Clear (CardSites.cs); resets all admission state.</summary>
    private void ResetAdmission()
    {
        _refusalsAtCap = 0;
        _pruneImmediately = true;
        _killsCount = 0;
        _suffixCount = 0;
        _suffixCountById.Clear();
    }

    /// <summary>Add a site if not already present (dedup by SlotAddr/Enc/IsKills/Id/AnchorAddr).
    /// Kills sites are gated by MaxSites with the rate-limited cap-relief prune. Suffix sites are
    /// gated by MaxSuffixSites and SuffixCopiesPerId, and a suffix refusal never prunes or touches
    /// _refusalsAtCap (it is routine once the cap is reached and PruneDeadSites is O(sites)).
    /// Returns true if added, false for a duplicate or a cap refusal.</summary>
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

    /// <summary>Evict every cached site (either kind) whose anchor is dead. Only Add's kills branch
    /// calls it. It bypasses the strike leniency because it must free room right now, and a
    /// possibly-transient Unreadable site would otherwise keep Add failing. PaintAll's maintenance
    /// passes keep the leniency. Each evicted site reaches _onPruneEvict (null is a no-op), which
    /// Display.Flight.cs turns into a "card" flight record under the existing _flightBudget tiers.
    /// PruneRearmFloor only decides whether the next refusal skips the rate limit.</summary>
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

    /// <summary>Remove sites from _sites, _keys, _strikes (no stale strike counts for a later
    /// site with the same key) and the per-kind counters, so capacity freed by either eviction
    /// path (this prune or PaintAll's strike evict) is available to the next Add of either kind.</summary>
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
