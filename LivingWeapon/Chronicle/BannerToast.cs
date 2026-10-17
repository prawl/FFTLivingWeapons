using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Tier-up and Weapon Chronicle milestone toast queue. Tally crossings enqueue; PromptSwap dequeues via TryTake when the
/// Wait-state facing prompt renders. Wording lives in BannerToast.Policy.cs. Enqueue runs on the loop thread and
/// TryPrepareSwap dequeues on the game's text-setter thread, so every _queue access goes through _lock.
/// The LWDEV DevProbe auto-toast and F2 DevTestKey cycle were removed because their fake payload kept firing in dev play.
/// A toast lives until its battle's exit edge (<see cref="DropPendingAtBattleEnd"/>); a sentinel flicker or an
/// enqueue during the exit sequence drops it too, costing one popup while the growth stays saved.
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
    // The tuple's tier slot is the dedupe key: tiers use 1..3, Chronicle milestones the negated value (-1, -100, -250, -500, -1000).
    // Bulwark (Sunderer +3, docs/BULWARK_AC.md) reserves key 4 (Tuning.BulwarkToastKey) so its direct Enqueue cannot collide.
    internal readonly List<(int weaponId, int tier, string payload)> _queue = new();

    /// <param name="enabled">Always on since LW-52 removed the toggle (Engine passes Tuning.BannerToasts); kept for the enabled-gate tests.</param>
    /// <remarks>Engine constructs this after the LWDEV dev-seed floors every tally, so snapshotting here baselines the seeded kills
    /// and no toast fires (Prime_never_fires_on_seeded_tally, Prime_swallows_milestones_on_preloaded_tally).</remarks>
    public BannerToast(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, bool enabled)
    {
        _meta = meta;
        _kills = kills;
        _enabled = enabled;
        Snapshot();
    }

    private void Snapshot()
    {
        foreach (int id in _meta.Keys)
        {
            _tiers[id] = Tuning.TierOf(_kills, id);
            _counts[id] = _kills.TryGetValue(id, out int k) ? k : 0;
        }
    }

    /// <summary>called on the PlaythroughReset new-game edge (same as Display.Invalidate, LW-59). drops pending
    /// toasts first, since they belong to the old playthrough. Then re-primes _tiers/_counts against the cleared kills without
    /// enqueuing. _tiers/_counts are loop-thread-only, so no lock.</summary>
    public void Rebaseline()
    {
        DropPendingAtBattleEnd();
        Snapshot();
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
            // The snapshot follows the tally both ways, so a rollback lowers the baseline and a later re-cross toasts again
            // (Dedupe_by_queue_membership_under_rollback).
            _tiers[id] = newTier;

            // Milestones are checked every pass so a milestone-only jump still fires; computed before the merge decision
            // so crossed and milestone reflect the same jump.
            int newKills = _kills.TryGetValue(id, out int k2) ? k2 : 0;
            int milestone = CrossedMilestone(_counts[id], newKills);
            _counts[id] = newKills;

            if (crossed != 0 && milestone == 1)
            {
                // First blood on the same kill as a tier crossing: one merged toast keyed on the tier. Milestones of 100+
                // cannot coincide with a first kill, so they take the two-toast path below.
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

            // FIFO: a same-jump tier toast enqueues before the milestone toast.
            if (milestone != 0 && !Contains(id, -milestone))
                Enqueue(id, -milestone, MilestonePayload(meta.Name, milestone));
        }
    }

    /// <summary>Locked membership check for the dedupe tests.</summary>
    private bool Contains(int weaponId, int tier)
    {
        lock (_lock) return _queue.Exists(q => q.weaponId == weaponId && q.tier == tier);
    }

    /// <summary>Locked enqueue; internal as the seam for Concurrent_enqueue_drop_and_drain_is_safe. Drops the oldest past QueueCap.</summary>
    internal void Enqueue(int weaponId, int tier, string payload)
    {
        lock (_lock)
        {
            _queue.Add((weaponId, tier, payload));
            // Static Flight.Record call; this class is too small to need an injected recorder.
            Flight.Record("toast", $"enqueued weapon={weaponId} tier={tier} payload=\"{payload}\"");
            if (_queue.Count <= QueueCap) return;
            // Stale news loses: drop the oldest.
            var dropped = _queue[0];
            _queue.RemoveAt(0);
            ModLogger.Event(LogVerb.Toast, $"The toast queue hit its cap of {QueueCap}; the oldest toast was dropped ({LogNames.Weapon(dropped.weaponId)}, tier {dropped.tier}).");
            Flight.Record("toast", $"dropped weapon={dropped.weaponId} tier={dropped.tier} (queue at cap {QueueCap})");
        }
    }

    /// <summary>Locked non-empty check.</summary>
    internal bool HasPending
    {
        get { lock (_lock) return _queue.Count > 0; }
    }

    /// <summary>Locked dequeue, PromptSwap's delivery seam (TryPrepareSwap, after the facing-prompt prefix matches).
    /// Returns false on an empty queue.</summary>
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

    /// <summary>drops every pending toast undelivered. Called early from Engine.ResetBattleState so the Flight records
    /// land on this battle's tape, and from <see cref="Rebaseline"/>. Logs one Event line per toast (like <see cref="Enqueue"/>'s
    /// drop-oldest) and one Flight "toast"/"dropped" record.</summary>
    internal void DropPendingAtBattleEnd()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            foreach (var (weaponId, tier, _) in _queue)
            {
                ModLogger.Event(LogVerb.Toast, $"A toast went undelivered by its battle's end and was dropped ({LogNames.Weapon(weaponId)}, tier {tier}); the growth itself is saved either way.");
                Flight.Record("toast", $"dropped weapon={weaponId} tier={tier} (undelivered at battle end)");
            }
            _queue.Clear();
        }
    }
}
