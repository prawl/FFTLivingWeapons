using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Native surface a BannerToast drives to hijack the game's own battle-callout bubble.
/// Candidates() returns the located callout holder(s) -- empty until BannerPipe's locate
/// succeeds; Commit swaps a validated payload into the holder's next in-flight show. Kept as a
/// seam so BannerToast is fully unit-testable without the native call: BannerPipe is the sole
/// production implementer, RecordingPipe (BannerToastTests) is the test double.</summary>
internal interface ICalloutPipe
{
    IReadOnlyList<long> Candidates();
    void Commit(long holder, string payload);
}

/// <summary>
/// Tier-up announcement via the live-proven battle-callout hijack (migrated from the dev spike
/// BannerSpike.cs into production 2026-07-02 -- see docs/LIVE_LEDGER.md Uncertain). A kill tally
/// crossing a tier queues a toast; the poll below re-commits the head of that queue onto the
/// game's own callout bubble the next time it naturally shows.
///
/// The poll is NEVER onField-gated: the spike's hard-won lesson (v6, 2026-07-02) is that the
/// callout shows during the ability-cast animation, when battleMode drops to 1 and onField reads
/// false -- an onField-gated poll sleeps through the exact window the show flag pulses, which is
/// how the spike's first live run missed every banner.
///
/// The native surface lives behind ICalloutPipe (BannerPipe in production) so this class never
/// touches game memory directly -- every path here is exercised by BannerToastTests against a
/// RecordingPipe test double.
/// </summary>
internal sealed partial class BannerToast
{
    // holder+0x88: pulses 1 for the banner's ~1s life, proven by a controller struct-diff during
    // a natural cast (BannerSpike provenance, 2026-07-02).
    private const long ShowFlagOffset = 0x88;
    private const int QueueCap = 8;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly ICalloutPipe _pipe;
    private readonly bool _enabled;
    private readonly IGameMemory _mem;

    private readonly Dictionary<int, int> _tiers = new();
    private readonly Dictionary<int, int> _counts = new();
    // EVENT-KEY CONVENTION: the tuple's tier slot doubles as the queue-dedupe event key. Tier
    // crossings use the real tier (1..3); Weapon Chronicle milestone crossings use the NEGATED
    // milestone (-1, -100, -250, -500, -1000) so the two kinds of event can never collide.
    internal readonly List<(int weaponId, int tier, string payload)> _queue = new();
    private readonly Dictionary<long, bool> _edge = new();

    /// <param name="enabled">Config.BannerToasts (or Tuning.BannerToasts fallback).</param>
    /// <remarks>PRIME AT CONSTRUCTION: Engine constructs this AFTER its LWDEV dev-seed block has
    /// already floored every weapon's tally, so snapshotting _tiers/_counts here from the CURRENT
    /// kills baselines the seeded tallies immediately -- a dev build's floor-every-weapon-to-P3
    /// seed never fires a tier toast (Prime_never_fires_on_seeded_tally) or a milestone toast
    /// (Prime_swallows_milestones_on_preloaded_tally).</remarks>
    public BannerToast(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, ICalloutPipe pipe,
                        bool enabled, IGameMemory? mem = null)
    {
        _meta = meta;
        _kills = kills;
        _pipe = pipe;
        _enabled = enabled;
        _mem = mem ?? new LiveMemory();
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
        Poll();
    }

    private void DetectCrossings()
    {
        foreach (int id in _meta.Keys)
        {
            int newTier = Tuning.TierOf(_kills, id);
            int crossed = CrossedTier(_tiers[id], newTier);
            // The snapshot follows the tally BOTH ways -- a tally rollback silently lowers the
            // baseline so a legit later re-cross toasts again (Dedupe_by_queue_membership_under_rollback).
            _tiers[id] = newTier;
            if (crossed != 0 && !_queue.Exists(q => q.weaponId == id && q.tier == crossed))   // dedupe: queue membership
            {
                var meta = _meta[id];
                string? sigLabel = crossed == 3 ? meta.Signature?.DisplayLabel : null;
                Enqueue(id, crossed, Payload(meta.Name, crossed, _kills[id], sigLabel));
            }

            // Weapon Chronicle: milestone check AFTER the tier block above, for EVERY weapon (not
            // just a crossing one) -- a same-jump crossing enqueues the tier toast first, so FIFO
            // shows tier before milestone.
            int newKills = _kills.TryGetValue(id, out int k2) ? k2 : 0;
            int milestone = CrossedMilestone(_counts[id], newKills);
            _counts[id] = newKills;
            if (milestone != 0 && !_queue.Exists(q => q.weaponId == id && q.tier == -milestone))
                Enqueue(id, -milestone, MilestonePayload(_meta[id].Name, milestone));
        }
    }

    private void Enqueue(int weaponId, int tier, string payload)
    {
        _queue.Add((weaponId, tier, payload));
        if (_queue.Count <= QueueCap) return;
        // Stale news loses: an overflowing queue drops the OLDEST toast, not the newest.
        var dropped = _queue[0];
        _queue.RemoveAt(0);
        Log.Info($"banner-toast: queue at cap ({QueueCap}) -- dropped stale toast for weapon {dropped.weaponId} tier {dropped.tier}");
    }

    private void Poll()
    {
        foreach (long addr in _pipe.Candidates())
        {
            bool up = _mem.U8(addr + ShowFlagOffset) == 1;
            // Two-sample edge: the FIRST observation of an address -- a freshly-located
            // candidate, or the first poll right after ResetBattle wiped _edge -- only baselines
            // and never fires, even if already high. Without this, a show flag held true across
            // a battle boundary would misread as a brand-new rise the instant the module
            // re-arms (Queue_survives_ResetBattle_and_edges_rearm).
            bool hasBaseline = _edge.TryGetValue(addr, out bool last);
            if (hasBaseline && up && !last && _queue.Count > 0)
            {
                _pipe.Commit(addr, _queue[0].payload);
                _queue.RemoveAt(0);
            }
            _edge[addr] = up;
        }
    }

    /// <summary>Re-arm for the next battle. The queue SURVIVES (user-locked carry policy: a
    /// battle-winning tier-up kill toasts next battle rather than being lost) -- only the
    /// per-candidate edge state resets, so a stale held-high flag can't misfire (see Poll).</summary>
    public void ResetBattle() => _edge.Clear();

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
        if (_queue.Exists(q => q.weaponId == id && q.tier == tier)) return;
        Enqueue(id, tier, payload);
        Log.Info($"banner-toast: DEV F2 test toast queued -- \"{payload}\"");
    }
#endif
}
