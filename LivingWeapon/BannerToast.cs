using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Tier-up / Weapon Chronicle milestone toast QUEUE. A kill tally crossing a tier or milestone
/// enqueues a toast; delivery is owned by PromptSwap (swaps the toast into the Wait-state facing
/// prompt the next time one renders -- a player-held UI slot, no scheduling contention), which
/// dequeues the head via TryTake once the facing-prompt text has matched. This class decides only
/// WHAT to say and WHEN it qualifies -- wording policy lives in BannerToast.Policy.cs.
///
/// The queue is cross-thread-safe (loop-thread enqueue via DetectCrossings; PromptSwap's
/// TryPrepareSwap dequeues from the game's own text-setter thread), so every touch point routes
/// through the three locked members below (_lock) so the queue stays safe regardless of which
/// thread reaches it. (The LWDEV DevProbe auto-toast and F2 DevTestKey cycle that verified the
/// delivery path live are REMOVED -- the fake "Zwill ... 999th kill ... Testfire!" payload kept
/// firing in normal dev play once per launch; the delivery mechanism is proven and the real
/// crossing detector is the only enqueuer now.)
/// </summary>
internal sealed partial class BannerToast
{
    private const int QueueCap = 8;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly bool _enabled;

    private readonly Dictionary<int, int> _tiers = new();
    private readonly Dictionary<int, int> _counts = new();
    private readonly object _lock = new();
    // EVENT-KEY CONVENTION: the tuple's tier slot doubles as the queue-dedupe event key. Tier
    // crossings use the real tier (1..3); Weapon Chronicle milestone crossings use the NEGATED
    // milestone (-1, -100, -250, -500, -1000) so the two kinds of event can never collide.
    internal readonly List<(int weaponId, int tier, string payload)> _queue = new();

    /// <param name="enabled">Config.BannerToasts (or Tuning.BannerToasts fallback).</param>
    /// <remarks>PRIME AT CONSTRUCTION: Engine constructs this AFTER its LWDEV dev-seed block has
    /// already floored every weapon's tally, so snapshotting _tiers/_counts here from the CURRENT
    /// kills baselines the seeded tallies immediately -- a dev build's floor-every-weapon-to-P3
    /// seed never fires a tier toast (Prime_never_fires_on_seeded_tally) or a milestone toast
    /// (Prime_swallows_milestones_on_preloaded_tally).</remarks>
    public BannerToast(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, bool enabled)
    {
        _meta = meta;
        _kills = kills;
        _enabled = enabled;
        foreach (int id in meta.Keys)
        {
            _tiers[id] = Tuning.TierOf(kills, id);
            _counts[id] = kills.TryGetValue(id, out int k) ? k : 0;
        }
    }

    public void Tick(bool tallyChanged)
    {
        if (!_enabled) return;
        if (tallyChanged) DetectCrossings();
    }

    private void DetectCrossings()
    {
        foreach (int id in _meta.Keys)
        {
            var meta = _meta[id];
            int newTier = Tuning.TierOf(_kills, id);
            int crossed = CrossedTier(_tiers[id], newTier);
            // The snapshot follows the tally BOTH ways -- a tally rollback silently lowers the
            // baseline so a legit later re-cross toasts again (Dedupe_by_queue_membership_under_rollback).
            _tiers[id] = newTier;

            // Weapon Chronicle milestone check runs every pass (not just a tier-crossing one) so a
            // milestone-only jump still fires. Computed here, ahead of the merge decision below, so
            // both crossed AND milestone reflect the SAME tally jump.
            int newKills = _kills.TryGetValue(id, out int k2) ? k2 : 0;
            int milestone = CrossedMilestone(_counts[id], newKills);
            _counts[id] = newKills;

            if (crossed != 0 && milestone == 1)
            {
                // First blood landing on the SAME kill that crosses a tier: ONE merged toast,
                // keyed on the tier crossing (not the milestone's negated key) -- the milestone-1
                // side is intentionally never separately enqueued. Higher milestones (100+) can
                // never coincide with a fresh weapon's very first kill, so they always fall
                // through to the two-toasts path below.
                if (!Contains(id, crossed))
                {
                    string? sigLabel = crossed == 3 ? meta.Signature?.DisplayLabel : null;
                    Enqueue(id, crossed, FirstBloodTierPayload(meta.Name, crossed, sigLabel));
                }
                continue;
            }

            if (crossed != 0 && !Contains(id, crossed))   // dedupe: queue membership
            {
                string? sigLabel = crossed == 3 ? meta.Signature?.DisplayLabel : null;
                Enqueue(id, crossed, Payload(meta.Name, crossed, _kills[id], sigLabel));
            }

            // FIFO: a same-jump tier crossing enqueues before the milestone toast.
            if (milestone != 0 && !Contains(id, -milestone))
                Enqueue(id, -milestone, MilestonePayload(meta.Name, milestone));
        }
    }

    /// <summary>Locked membership check -- DetectCrossings' dedupe checks route through here so
    /// no queue read happens outside the lock.</summary>
    private bool Contains(int weaponId, int tier)
    {
        lock (_lock) return _queue.Exists(q => q.weaponId == weaponId && q.tier == tier);
    }

    /// <summary>Locked enqueue. Internal: also the concurrency-hammer test seam
    /// (Concurrent_enqueue_and_drain_is_safe). QueueCap drop-oldest stays inside the lock.</summary>
    internal void Enqueue(int weaponId, int tier, string payload)
    {
        lock (_lock)
        {
            _queue.Add((weaponId, tier, payload));
            if (_queue.Count <= QueueCap) return;
            // Stale news loses: an overflowing queue drops the OLDEST toast, not the newest.
            var dropped = _queue[0];
            _queue.RemoveAt(0);
            ModLogger.Log($"banner-toast: queue at cap ({QueueCap}) -- dropped stale toast for weapon {dropped.weaponId} tier {dropped.tier}");
        }
    }

    /// <summary>Locked non-empty check, kept as a general-purpose queue-state read so no caller
    /// ever reaches into _queue outside _lock.</summary>
    internal bool HasPending
    {
        get { lock (_lock) return _queue.Count > 0; }
    }

    /// <summary>Locked dequeue -- PromptSwap's delivery seam (TryPrepareSwap, once the facing-
    /// prompt prefix has matched). Empty queue is a fast no-op (false); no PutBack -- the prefix
    /// match gates every call, so a mismatched prompt never touches the queue.</summary>
    internal bool TryTake(out (int weaponId, int tier, string payload) toast)
    {
        lock (_lock)
        {
            if (_queue.Count == 0) { toast = default; return false; }
            toast = _queue[0];
            _queue.RemoveAt(0);
            return true;
        }
    }

}
