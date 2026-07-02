using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The tier-up toast: BannerToast.cs (driver -- prime/queue/poll) + BannerToast.Policy.cs (pure
/// wording/crossing decisions) + BannerPipe.cs (the isolated native surface, exercised here only
/// via its locate/revalidate logic against FakeHeap -- Commit's native call is never invoked by
/// a unit test, by construction: BannerPipe.Commit is only reachable through ICalloutPipe, and
/// the driver suite below drives it through RecordingPipe, never the real BannerPipe).
///
/// RecordingPipe is a test-only ICalloutPipe: a settable candidate list plus a recorded Commit
/// call log, so the driver's queue/poll/dedupe logic is provable without any native surface.
/// </summary>
public class BannerToastTests
{
    private sealed class RecordingPipe : ICalloutPipe
    {
        public readonly List<long> Members = new();
        public readonly List<(long holder, string payload)> Commits = new();
        public IReadOnlyList<long> Candidates() => Members;
        public void Commit(long holder, string payload) => Commits.Add((holder, payload));
    }

    private static WeaponMeta Meta(string name, WeaponSignature? sig = null)
        => new() { Name = name, Signature = sig };

    private static void WriteQwordPair(byte[] buf, int offset, ulong a, ulong b)
    {
        BitConverter.GetBytes(a).CopyTo(buf, offset);
        BitConverter.GetBytes(b).CopyTo(buf, offset + 8);
    }

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
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        bt.Tick(tallyChanged: true);

        Assert.Empty(bt._queue);
        Assert.Empty(pipe.Commits);
    }

    // ---- (2) A genuine crossing enqueues once with the locked wording ----

    [Fact]
    public void Crossing_enqueues_once_with_locked_wording()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Zwill Straightblade") };
        // Primed already-blooded (1, not 0) so this isolates the TIER crossing from the
        // orthogonal Weapon Chronicle first-blood milestone (a true zero start also crosses
        // milestone 1 -- see First_blood_fires_once_and_rides_the_pipe).
        var kills = new Dictionary<int, int> { [1] = 1 };
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        kills[1] = Tuning.ProdThresholds[0];   // 5 -- crosses tier 1
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal("Zwill Straightblade has gained its 5th kill and has grown to +!", bt._queue[0].payload);
    }

    // ---- (3) A multi-tier jump (dev seed / fast-forward) produces ONE toast at the top ----

    [Fact]
    public void Multi_tier_jump_produces_one_toast_at_highest_tier()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Warblade") };
        // Primed already-blooded (1, not 0) -- see the comment in Crossing_enqueues_once_with_locked_wording.
        var kills = new Dictionary<int, int> { [1] = 1 };
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

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
        var kills = new Dictionary<int, int> { [1] = 0, [2] = 0 };
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        kills[1] = Tuning.ProdThresholds[2];
        bt.Tick(tallyChanged: true);
        kills[2] = Tuning.ProdThresholds[2];
        bt.Tick(tallyChanged: true);

        var sig = bt._queue.Find(q => q.weaponId == 1);
        var growth = bt._queue.Find(q => q.weaponId == 2);
        string ord = BannerToast.OrdinalSuffix(Tuning.ProdThresholds[2]);
        Assert.Equal($"Kiyomori has gained its {Tuning.ProdThresholds[2]}{ord} kill and has unlocked Kobu!", sig.payload);
        Assert.Equal($"Plainblade has gained its {Tuning.ProdThresholds[2]}{ord} kill and has grown to +3!", growth.payload);
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

    // ---- (6) LOAD-BEARING: two queued toasts commit FIFO, exactly once per show edge ----

    [Fact]
    public void Two_queued_toasts_commit_FIFO_exactly_once_per_show_edge()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Alpha"), [2] = Meta("Beta") };
        // Primed already-blooded (1, not 0) -- see the comment in Crossing_enqueues_once_with_locked_wording.
        var kills = new Dictionary<int, int> { [1] = 1, [2] = 1 };
        var pipe = new RecordingPipe();
        var mem = new FakeSparseMemory();
        long holder = 0x2000;
        pipe.Members.Add(holder);
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: mem);

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(tallyChanged: true);   // queues toast #1; also polls (flag 0 -- baselines)
        kills[2] = Tuning.ProdThresholds[0];
        bt.Tick(tallyChanged: true);   // queues toast #2; also polls (flag still 0)

        Assert.Equal(2, bt._queue.Count);

        mem.U8s[holder + 0x88] = 1;
        bt.Tick(tallyChanged: false);   // rising edge: exactly one commit

        Assert.Single(pipe.Commits);
        Assert.StartsWith("Alpha", pipe.Commits[0].payload);
        Assert.Single(bt._queue);

        mem.U8s[holder + 0x88] = 0;
        bt.Tick(tallyChanged: false);   // falling edge
        mem.U8s[holder + 0x88] = 1;
        bt.Tick(tallyChanged: false);   // rising edge again: second commit

        Assert.Equal(2, pipe.Commits.Count);
        Assert.StartsWith("Beta", pipe.Commits[1].payload);
        Assert.Empty(bt._queue);
    }

    // ---- (7) The non-vacuous negative: a rising edge with an empty queue never commits ----

    [Fact]
    public void Rising_edge_with_empty_queue_never_commits()
    {
        var meta = new Dictionary<int, WeaponMeta>();
        var kills = new Dictionary<int, int>();
        var pipe = new RecordingPipe();
        var mem = new FakeSparseMemory();
        long holder = 0x3000;
        pipe.Members.Add(holder);
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: mem);

        bt.Tick(false);              // baseline: flag=0
        mem.U8s[holder + 0x88] = 1;
        bt.Tick(false);              // rising edge, but the queue is empty

        Assert.Empty(pipe.Commits);
    }

    // ---- (8) The queue survives ResetBattle; a stale held-high edge does not double-fire ----

    [Fact]
    public void Queue_survives_ResetBattle_and_edges_rearm()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Alpha") };
        // Primed already-blooded (1, not 0) -- see the comment in Crossing_enqueues_once_with_locked_wording.
        var kills = new Dictionary<int, int> { [1] = 1 };
        var pipe = new RecordingPipe();
        var mem = new FakeSparseMemory();
        long holder = 0x4000;
        pipe.Members.Add(holder);
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: mem);

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);                 // queues the toast; polls flag=0, baselines

        mem.U8s[holder + 0x88] = 1;    // flag rises and STAYS held straight into the reset
                                        // (no Tick here -- simulate the reset landing mid-show,
                                        // before this module ever observed the rise)
        bt.ResetBattle();              // edge state wiped; the queued toast SURVIVES

        Assert.Single(bt._queue);

        bt.Tick(false);                // first post-reset poll: flag still 1, but no prior
                                        // baseline -- must NOT read as a fresh rise
        Assert.Empty(pipe.Commits);
        Assert.Single(bt._queue);

        mem.U8s[holder + 0x88] = 0;
        bt.Tick(false);                // falling edge
        mem.U8s[holder + 0x88] = 1;
        bt.Tick(false);                // genuine 1->0->1 rise: exactly one commit

        Assert.Single(pipe.Commits);
        Assert.Empty(bt._queue);
    }

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
        var pipe = new RecordingPipe();
        var mem = new FakeSparseMemory();
        long holder = 0x5000;
        pipe.Members.Add(holder);
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: mem);

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);                 // crosses tier 1 -- queued
        Assert.Single(bt._queue);

        kills[1] = 1;                  // rollback below threshold
        bt.Tick(true);                 // snapshot follows down; no new entry
        Assert.Single(bt._queue);

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);                 // re-crosses while the first toast is STILL queued -- deduped
        Assert.Single(bt._queue);

        // Commit the queued toast.
        mem.U8s[holder + 0x88] = 1;
        bt.Tick(false);
        Assert.Empty(bt._queue);
        Assert.Single(pipe.Commits);

        // A fresh cross after the commit is a LEGIT re-toast (the queue no longer holds it).
        mem.U8s[holder + 0x88] = 0;
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
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

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
        var pipe = new RecordingPipe();
        var mem = new FakeSparseMemory();
        long holder = 0x6000;
        pipe.Members.Add(holder);
        mem.U8s[holder + 0x88] = 1;
        var bt = new BannerToast(meta, kills, pipe, enabled: false, mem: mem);

        kills[1] = Tuning.ProdThresholds[2];
        bt.Tick(true);
        bt.Tick(false);

        Assert.Empty(bt._queue);
        Assert.Empty(pipe.Commits);
    }

    // ---- (12) No candidates located yet is inert (no crash, nothing to poll) ----

    [Fact]
    public void No_candidates_is_inert()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Alpha") };
        // Primed already-blooded (1, not 0) -- see the comment in Crossing_enqueues_once_with_locked_wording.
        var kills = new Dictionary<int, int> { [1] = 1 };
        var pipe = new RecordingPipe();   // Members left empty -- BannerPipe never located
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        kills[1] = Tuning.ProdThresholds[0];
        bt.Tick(true);
        bt.Tick(false);

        Assert.Single(bt._queue);          // still queued -- nothing to poll against
        Assert.Empty(pipe.Commits);
    }

    // ---- (13) BannerPipe locate against FakeHeap: the unique holder wins among decoys ----

    [Fact]
    public void BannerPipe_locates_the_unique_holder_among_decoys()
    {
        long baseAddr = 0x10000;
        var buf = new byte[0x400];
        WriteQwordPair(buf, 0x40, 0xDEADBEEFCAFEUL, BannerPipe.HolderId);           // wrong vtable, right id
        WriteQwordPair(buf, 0x80, (ulong)BannerPipe.HolderVtable, 0x111UL);         // right vtable, wrong id
        int realOffset = 0x100;
        WriteQwordPair(buf, realOffset, (ulong)BannerPipe.HolderVtable, BannerPipe.HolderId);   // the real holder

        var heap = new FakeHeap((baseAddr, buf));
        var pipe = new BannerPipe(heap);

        for (int i = 0; i < 305; i++) pipe.Tick();

        Assert.Equal(new[] { baseAddr + realOffset }, pipe.Candidates());
    }

    [Fact]
    public void BannerPipe_two_valid_fabrications_fail_closed()
    {
        long baseAddr = 0x20000;
        var buf = new byte[0x400];
        WriteQwordPair(buf, 0x100, (ulong)BannerPipe.HolderVtable, BannerPipe.HolderId);
        WriteQwordPair(buf, 0x200, (ulong)BannerPipe.HolderVtable, BannerPipe.HolderId);

        var heap = new FakeHeap((baseAddr, buf));
        var pipe = new BannerPipe(heap);

        for (int i = 0; i < 305; i++) pipe.Tick();

        Assert.Empty(pipe.Candidates());
    }

    [Fact]
    public void BannerPipe_zero_candidates_stays_empty()
    {
        long baseAddr = 0x30000;
        var buf = new byte[0x400];   // no pattern anywhere

        var heap = new FakeHeap((baseAddr, buf));
        var pipe = new BannerPipe(heap);

        for (int i = 0; i < 305; i++) pipe.Tick();

        Assert.Empty(pipe.Candidates());
    }

    // ---- (14) BannerPipe revalidation drops a corrupted cache and re-arms the scan ----

    [Fact]
    public void BannerPipe_revalidation_drops_cache_and_rearms_scan()
    {
        long baseAddr = 0x40000;
        var buf = new byte[0x400];
        int offset = 0x100;
        WriteQwordPair(buf, offset, (ulong)BannerPipe.HolderVtable, BannerPipe.HolderId);

        var heap = new FakeHeap((baseAddr, buf));
        var pipe = new BannerPipe(heap);
        for (int i = 0; i < 305; i++) pipe.Tick();
        Assert.Single(pipe.Candidates());

        // Corrupt the id qword directly in the fake's backing storage.
        var region = heap.RegionBytes(baseAddr)!;
        BitConverter.GetBytes(0xBADUL).CopyTo(region, offset + 8);

        pipe.ResetBattle();

        Assert.Empty(pipe.Candidates());
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
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        bt.Tick(tallyChanged: true);
        Assert.Empty(bt._queue);

        kills[1] = 250;
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal(BannerToast.MilestonePayload("Kiyomori", 250), bt._queue[0].payload);
    }

    // ---- (18) LOAD-BEARING: first blood fires once and rides the callout pipe ----

    [Fact]
    public void First_blood_fires_once_and_rides_the_pipe()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var pipe = new RecordingPipe();
        var mem = new FakeSparseMemory();
        long holder = 0x7000;
        pipe.Members.Add(holder);
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: mem);

        kills[1] = 1;
        bt.Tick(tallyChanged: true);

        Assert.Single(bt._queue);
        Assert.Equal("Kiyomori draws first blood!", bt._queue[0].payload);

        mem.U8s[holder + 0x88] = 1;
        bt.Tick(tallyChanged: false);   // rising edge: commit

        Assert.Single(pipe.Commits);
        Assert.Equal("Kiyomori draws first blood!", pipe.Commits[0].payload);
        Assert.Empty(bt._queue);

        mem.U8s[holder + 0x88] = 0;
        bt.Tick(tallyChanged: false);   // falling edge
        mem.U8s[holder + 0x88] = 1;
        bt.Tick(tallyChanged: false);   // rising edge again, but the queue is empty

        Assert.Single(pipe.Commits);
    }

    // ---- (19) A same-jump tier crossing enqueues BEFORE the milestone (FIFO shows tier first) ----

    [Fact]
    public void Tier_and_milestone_same_jump_enqueue_tier_first()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        kills[1] = 105;   // crosses tier 3 (ProdThresholds[2]=50) AND milestone 100
        bt.Tick(tallyChanged: true);

        Assert.Equal(2, bt._queue.Count);
        Assert.Equal(3, bt._queue[0].tier);
        Assert.Equal(-100, bt._queue[1].tier);
        string ord = BannerToast.OrdinalSuffix(105);
        Assert.Equal($"Kiyomori has gained its 105{ord} kill and has grown to +3!", bt._queue[0].payload);
        Assert.Equal("Kiyomori claims its 100th soul!", bt._queue[1].payload);
    }

    // ---- (20) Milestone dedupe is queue-membership, same as tier ----

    [Fact]
    public void Milestone_dedupe_by_queue_membership()
    {
        var meta = new Dictionary<int, WeaponMeta> { [1] = Meta("Kiyomori") };
        var kills = new Dictionary<int, int> { [1] = 0 };
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

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
        var pipe = new RecordingPipe();
        var bt = new BannerToast(meta, kills, pipe, enabled: true, mem: new FakeSparseMemory());

        kills[1] = Tuning.DevKillSeed + 1;
        bt.Tick(tallyChanged: true);

        Assert.Empty(bt._queue);
    }
}
