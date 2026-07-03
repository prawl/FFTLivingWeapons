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
/// The queue is cross-thread-safe (loop-thread enqueue via DetectCrossings/the LWDEV DevProbe/
/// DevTestKey; PromptSwap.TryPrepareSwap dequeues from the game's own text-setter thread), so every
/// touch point routes through the three locked members below (_lock) so the queue stays safe
/// regardless of which thread reaches it.
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
#if LWDEV
        DevProbe();
        DevTestKey();
#endif
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

    /// <summary>Locked membership check -- both DetectCrossings' dedupe checks and DevTestKey's
    /// cycle guard route through here so no queue read happens outside the lock.</summary>
    private bool Contains(int weaponId, int tier)
    {
        lock (_lock) return _queue.Exists(q => q.weaponId == weaponId && q.tier == tier);
    }

    /// <summary>Locked enqueue. Internal: also the concurrency-hammer test seam
    /// (Concurrent_enqueue_and_drain_is_safe), so DevProbe/DevTestKey inherit thread safety for
    /// free. QueueCap drop-oldest stays inside the lock.</summary>
    internal void Enqueue(int weaponId, int tier, string payload)
    {
        lock (_lock)
        {
            _queue.Add((weaponId, tier, payload));
            if (_queue.Count <= QueueCap) return;
            // Stale news loses: an overflowing queue drops the OLDEST toast, not the newest.
            var dropped = _queue[0];
            _queue.RemoveAt(0);
            Log.Info($"banner-toast: queue at cap ({QueueCap}) -- dropped stale toast for weapon {dropped.weaponId} tier {dropped.tier}");
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

#if LWDEV
    private const int ProbeAfterTicks = 300;
    private int _probeTick;
    private bool _probed;   // never reset -- once per LAUNCH, not once per battle

    /// <summary>DEV verification probe (live review blocker 1): a MAX-LENGTH-SHAPED toast (the
    /// spike's payload was a 13-char "BUBBA LUVS +3") exercises the game-side heap-alloc string
    /// path the short spike payload never touched.</summary>
    private void DevProbe()
    {
        if (_probed) return;
        if (++_probeTick < ProbeAfterTicks) return;
        _probed = true;
        Enqueue(0, 3, "Zwill Straightblade has gained its 999th kill and has unlocked Testfire!");
        Log.Info("banner-toast: DEV probe queued (max-length payload)");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkF2 = 0x71;
    private bool _f2Was;
    private int _f2Cycle;

    /// <summary>DEV on-demand test key: each F2 press queues the next payload in a cycle of the
    /// REAL wording shapes (max-length heap-path stress, tier-1 bare-plus, tier-3 signature,
    /// Weapon Chronicle milestone) so every variant can be seen and screenshotted on a live
    /// callout without engineering kills. Distinct synthetic weaponIds (0/-1/-2/-3) keep the
    /// cycle out of the dedupe's way; pressing F2 twice on the SAME variant while it is still
    /// queued dedupes by design. GetAsyncKeyState is a global key-state query, safe from the
    /// loop thread; the tick only runs in-battle so out-of-battle presses are naturally inert.</summary>
    private void DevTestKey()
    {
        bool down = (GetAsyncKeyState(VkF2) & 0x8000) != 0;
        bool pressed = down && !_f2Was;
        _f2Was = down;
        if (!pressed) return;
        var (id, tier, payload) = (_f2Cycle++ % 4) switch
        {
            0 => (0, 3, "Zwill Straightblade has gained its 999th kill and has unlocked Testfire!"),
            1 => (-1, 1, Payload("Kiyomori", 1, 5, null)),
            2 => (-2, 3, Payload("Kiyomori", 3, 50, "Kobu")),
            _ => (-3, -1000, MilestonePayload("Kiyomori", 1000)),
        };
        if (Contains(id, tier)) return;
        Enqueue(id, tier, payload);
        Log.Info($"banner-toast: DEV F2 test toast queued -- \"{payload}\"");
    }
#endif
}
