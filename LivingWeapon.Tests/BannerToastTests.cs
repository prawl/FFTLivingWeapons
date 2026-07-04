using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The tier-up / Weapon Chronicle toast QUEUE: BannerToast.cs (driver -- prime/crossing-detect/
/// dequeue) + BannerToast.Policy.cs (pure wording/crossing decisions).
///
/// Delivery (dequeue -> swap) is owned by PromptSwap.cs (swaps a queued toast into the Wait-state
/// facing prompt the next time one renders) -- PromptSwapTests.cs owns that behavior. This file
/// owns priming, crossing/dedupe detection, wording, and the queue's own FIFO/cap contract
/// (Enqueue/TryTake), asserted directly against `bt._queue` / `bt.TryTake` rather than a recorded
/// commit log.
/// </summary>
public class BannerToastTests
{
    private static WeaponMeta Meta(string name, WeaponSignature? sig = null)
        => new() { Name = name, Signature = sig };

    // ---- (1) Prime-at-construction: a dev-seeded tally must never toast ----

    [Fact]
    public void Prime_never_fires_on_seeded_tally()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            [1] = Meta("Alpha"),
            [2] = Meta("Beta"),
            [3] = Meta("Gamma"),
        };
        var kills = new Dictionary<int, int>
        {
            [1] = Tuning.ProdThresholds[2],
            [2] = Tuning.ProdThresholds[2],
            [3] = Tuning.ProdThresholds[2],
        };
        var bt = new BannerToast(meta, kills, enabled: true);

        bt.Tick(tallyChanged: true);

        Assert.Empty(bt._queue);
    }

    // ---- (2) A genuine crossing enqueues once with the locked wording ----

    [Fact]
    public void Crossing_enqueues_once_with_locked_wording()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Zwill Straightblade") };
        // Primed already-blooded (1, not 0) so this isolates the TIER crossing from the
        // orthogonal Weapon Chronicle first-blood milestone (a true zero start also crosses
        // milestone 1 -- see First_blood_enqueues_locked_payload).
        var kills = new Dictionary<int, int> { [1] = 1 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = Tuning.ProdThresholds[0];   // 5 -- crosses tier 1
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal("Zwill Straightblade has gained its 5th kill and has grown to Zwill Straightblade+", bt._queue[0].payload);
    }

    // ---- (3) A multi-tier jump (dev seed / fast-forward) produces ONE toast at the top ----

    [Fact]
    public void Multi_tier_jump_produces_one_toast_at_highest_tier()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Warblade") };
        // Primed already-blooded (1, not 0) -- see the comment in Crossing_enqueues_once_with_locked_wording.
        var kills = new Dictionary<int, int> { [1] = 1 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = Tuning.ProdThresholds[2];   // jump straight past tier 1 and 2
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal(3, bt._queue[0].tier);
    }

    // ---- (4) Tier-3 signature weapon announces the unlock; a growth-only weapon announces the suffix ----

    [Fact]
    public void Signature_weapon_unlock_wording_at_tier3()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            [1] = Meta("Kiyomori", new WeaponSignature { DisplayLabel = "Kobu", AtTier = 3 }),
            [2] = Meta("Plainblade"),
        };
        // Primed already-blooded (1, not 0) so this isolates the TIER crossing from the orthogonal
        // Weapon Chronicle first-blood milestone -- see the comment in
        // Crossing_enqueues_once_with_locked_wording (a true zero start would also cross milestone
        // 1 and MERGE with the tier crossing, per FirstBlood_and_tier_same_pass_merge_into_one_toast).
        var kills = new Dictionary<int, int> { [1] = 1, [2] = 1 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = Tuning.ProdThresholds[2];
        bt.Tick(tallyChanged: true);
        kills[2] = Tuning.ProdThresholds[2];
        bt.Tick(tallyChanged: true);

        var sig = bt._queue.Find(q => q.weaponId == 1);
        var growth = bt._queue.Find(q => q.weaponId == 2);
        string ord = BannerToast.OrdinalSuffix(Tuning.ProdThresholds[2]);
        Assert.Equal($"Kiyomori has gained its {Tuning.ProdThresholds[2]}{ord} kill and has unlocked Kobu!", sig.payload);
        Assert.Equal($"Plainblade has gained its {Tuning.ProdThresholds[2]}{ord} kill and has grown to Plainblade+3", growth.payload);
    }

    // ---- (5) OrdinalSuffix: pure policy ----

    [Theory]
    [InlineData(1, "st")]
    [InlineData(2, "nd")]
    [InlineData(3, "rd")]
    [InlineData(4, "th")]
    [InlineData(11, "th")]
    [InlineData(12, "th")]
    [InlineData(13, "th")]
    [InlineData(21, "st")]
    [InlineData(22, "nd")]
    [InlineData(23, "rd")]
    [InlineData(101, "st")]
    [InlineData(111, "th")]
    public void OrdinalSuffix_covers_teens_and_digits(int n, string expected)
        => Assert.Equal(expected, BannerToast.OrdinalSuffix(n));

    // ---- (9) Dedupe is queue-membership, not tier history -- a rollback re-arms a re-cross ----

    [Fact]
    public void Dedupe_by_queue_membership_under_rollback()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Alpha") };
        // Primed already-blooded (1, not 0), and every "rollback below threshold" below targets 1,
        // not 0 -- this test is scoped to TIER dedupe under rollback; a literal-zero rollback would
        // also legitimately re-arm the orthogonal Weapon Chronicle first-blood milestone (see
        // Milestone_dedupe_by_queue_membership).
        var kills = new Dictionary<int, int> { [1] = 1 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);                 // crosses tier 1 -- queued
        Assert.Single(bt._queue);

        kills[1] = 1;                  // rollback below threshold
        bt.Tick(true);                 // snapshot follows down; no new entry
        Assert.Single(bt._queue);

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);                 // re-crosses while the first toast is STILL queued -- deduped
        Assert.Single(bt._queue);

        // Deliver (drain) the queued toast -- PromptSwap.TryPrepareSwap's TryTake in production.
        Assert.True(bt.TryTake(out _));
        Assert.Empty(bt._queue);

        // A fresh cross after delivery is a LEGIT re-toast (the queue no longer holds it).
        kills[1] = 1;
        bt.Tick(true);
        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);

        Assert.Single(bt._queue);
    }

    // ---- (10) Cap drops the OLDEST toast, keeps the newest ----

    [Fact]
    public void Cap_drops_oldest()
    {
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int>();
        for (int i = 1; i <= 9; i++)
        {
            meta[i] = Meta($"Weapon{i}");
            kills[i] = 0;
        }
        var bt = new BannerToast(meta, kills, enabled: true);

        for (int i = 1; i <= 9; i++)
        {
            kills[i] = Tuning.ProdThresholds[0];
            bt.Tick(true);
        }

        Assert.Equal(8, bt._queue.Count);
        Assert.DoesNotContain(bt._queue, q => q.weaponId == 1);   // oldest dropped
        Assert.Contains(bt._queue, q => q.weaponId == 9);          // newest present
    }

    // ---- (11) A disabled config is fully inert ----

    [Fact]
    public void Disabled_config_is_fully_inert()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Alpha") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: false);

        kills[1] = Tuning.ProdThresholds[2];
        bt.Tick(true);
        bt.Tick(false);

        Assert.Empty(bt._queue);
    }

    // ---- (15) Weapon Chronicle: locked milestone wording ----

    [Theory]
    [InlineData(1, "Kiyomori draws first blood!")]
    [InlineData(100, "Kiyomori claims its 100th soul!")]
    [InlineData(250, "Kiyomori has felled 250 foes!")]
    [InlineData(500, "500 souls rest upon Kiyomori's edge!")]
    [InlineData(1000, "Kiyomori, slayer of a thousand!")]
    public void MilestonePayloads_match_locked_wording(int milestone, string expected)
        => Assert.Equal(expected, BannerToast.MilestonePayload("Kiyomori", milestone));

    // ---- (16) CrossedMilestone: pure policy ----

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(0, 105, 100)]
    [InlineData(99, 260, 250)]
    [InlineData(999, 1000, 1000)]
    [InlineData(100, 100, 0)]
    [InlineData(250, 999, 500)]
    [InlineData(3, 50, 0)]
    [InlineData(1, 99, 0)]
    [InlineData(3, 4, 0)]
    public void CrossedMilestone_semantics(int prevKills, int newKills, int expected)
        => Assert.Equal(expected, BannerToast.CrossedMilestone(prevKills, newKills));

    // ---- (17) Prime-at-construction swallows a milestone already behind a preloaded tally ----

    [Fact]
    public void Prime_swallows_milestones_on_preloaded_tally()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 150 };
        var bt = new BannerToast(meta, kills, enabled: true);

        bt.Tick(tallyChanged: true);
        Assert.Empty(bt._queue);

        kills[1] = 250;
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal(BannerToast.MilestonePayload("Kiyomori", 250), bt._queue[0].payload);
    }

    // ---- (18) First blood enqueues the locked wording (the delivery half now lives in
    // PromptSwapTests.Facing_prompt_with_pending_toast_swaps_and_dequeues) ----

    [Fact]
    public void First_blood_enqueues_locked_payload()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = 1;
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal("Kiyomori draws first blood!", bt._queue[0].payload);
    }

    // ---- (19) A same-jump tier crossing enqueues BEFORE the milestone (FIFO shows tier first) ----

    [Fact]
    public void Tier_and_milestone_same_jump_enqueue_tier_first()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = 105;   // crosses tier 3 (ProdThresholds[2]=50) AND milestone 100
        bt.Tick(tallyChanged: true);

        Assert.Equal(2, bt._queue.Count);
        Assert.Equal(3, bt._queue[0].tier);
        Assert.Equal(-100, bt._queue[1].tier);
        string ord = BannerToast.OrdinalSuffix(105);
        Assert.Equal($"Kiyomori has gained its 105{ord} kill and has grown to Kiyomori+3", bt._queue[0].payload);
        Assert.Equal("Kiyomori claims its 100th soul!", bt._queue[1].payload);
    }

    // ---- (20) Milestone dedupe is queue-membership, same as tier ----

    [Fact]
    public void Milestone_dedupe_by_queue_membership()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = 100;
        bt.Tick(tallyChanged: true);   // crosses tier 3 AND milestone 100 -- both queued
        Assert.Single(bt._queue.FindAll(q => q.tier == -100));

        kills[1] = 60;                 // roll back below the milestone (still tier 3)
        bt.Tick(tallyChanged: true);
        kills[1] = 100;                // re-cross 100 while the first milestone toast is still queued
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue.FindAll(q => q.tier == -100));
    }

    // ---- (21) The dev-seed floor (3) is below every milestone, so a seeded weapon never first-bloods ----

    [Fact]
    public void Dev_seed_floor_never_first_bloods()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = Tuning.DevKillSeed };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = Tuning.DevKillSeed + 1;
        bt.Tick(tallyChanged: true);

        Assert.Empty(bt._queue);
    }

    // ---- (22) TryTake is FIFO; an empty queue returns false ----

    [Fact]
    public void TryTake_is_FIFO_and_empty_returns_false()
    {
        var bt = new BannerToast(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), enabled: true);

        Assert.False(bt.TryTake(out _));

        bt.Enqueue(1, 1, "first");
        bt.Enqueue(2, 1, "second");

        Assert.True(bt.TryTake(out var t1));
        Assert.Equal("first", t1.payload);
        Assert.True(bt.TryTake(out var t2));
        Assert.Equal("second", t2.payload);
        Assert.False(bt.TryTake(out _));
    }

    // ---- (23) HasPending tracks the queue through enqueue and drain (a locked read so no
    // caller ever reaches into _queue directly) ----

    [Fact]
    public void HasPending_tracks_enqueue_and_drain()
    {
        var bt = new BannerToast(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), enabled: true);

        Assert.False(bt.HasPending);

        bt.Enqueue(1, 1, "toast");
        Assert.True(bt.HasPending);

        Assert.True(bt.TryTake(out _));
        Assert.False(bt.HasPending);
    }

    // ---- (24) First blood + a tier crossing in the SAME pass merge into ONE toast, keyed on the
    // tier (not the milestone) ----

    [Fact]
    public void FirstBlood_and_tier_same_pass_merge_into_one_toast()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori", new WeaponSignature { DisplayLabel = "Kobu", AtTier = 3 }) };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: true);

        // A fresh weapon's first tier-1 crossing ALSO crosses milestone 1 (first blood) from a
        // true zero start -- true under either build flavor (DevThresholds[0]=1 or
        // ProdThresholds[0]=5; CrossedMilestone returns 1 for any newKills in [1,99]).
        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        var (weaponId, tier, payload) = bt._queue[0];
        Assert.Equal(1, weaponId);
        Assert.Equal(1, tier);   // event key = the TIER key, not the milestone's negated key
        Assert.Equal("Kiyomori draws first blood and has grown to Kiyomori+", payload);
    }

    // ---- (25) First blood alone (no tier crossing) still fires the plain milestone wording ----

    [Fact]
    public void FirstBlood_alone_still_fires()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: true);

        // Under ProdThresholds (5/25/50), a single kill (0->1) crosses milestone 1 alone; tier
        // stays 0 -- the CrossedMilestone-only pass the merge logic must leave untouched.
        kills[1] = 1;
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal(-1, bt._queue[0].tier);
        Assert.Equal("Kiyomori draws first blood!", bt._queue[0].payload);
    }

    // ---- (26) A higher milestone (100+) never merges with a same-jump tier crossing -- two
    // toasts queue, tier first (existing FIFO doc behavior) ----

    [Fact]
    public void Higher_milestones_do_not_merge()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Materia Blade") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var bt = new BannerToast(meta, kills, enabled: true);

        kills[1] = 105;   // crosses tier 3 (ProdThresholds[2]=50) AND milestone 100
        bt.Tick(tallyChanged: true);

        Assert.Equal(2, bt._queue.Count);
        Assert.Equal(3, bt._queue[0].tier);
        Assert.Equal(-100, bt._queue[1].tier);
    }

    // ---- (27) FirstBloodTierPayload: direct Policy wording, both arms ----

    [Theory]
    [InlineData(1, null, "Kiyomori draws first blood and has grown to Kiyomori+")]
    [InlineData(2, null, "Kiyomori draws first blood and has grown to Kiyomori+2")]
    [InlineData(3, null, "Kiyomori draws first blood and has grown to Kiyomori+3")]
    [InlineData(3, "Kobu", "Kiyomori draws first blood and has unlocked Kobu!")]
    public void FirstBloodTierPayload_wording(int tier, string? sigLabel, string expected)
        => Assert.Equal(expected, BannerToast.FirstBloodTierPayload("Kiyomori", tier, sigLabel));
}
