using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-452: the flee restore and the mid battle checkpoint copy rewind the vanilla bag but not
/// the extended counts. ProcessCheckpoint and ProcessRewind are the detour bodies with the game's
/// routine injected as a lambda.</summary>
public class RewindRestoreHookTests
{
    private const long Bag = 0x1000;
    private static readonly int[] Ids = { 261, 262, 263, 264, 265, 266, 267 };

    private const long Roster = 0x20000;
    private const int Stride = 0x258;

    private static FakeCodePatcher Bagged(params byte[] counts)
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.Seed(Bag + 261, counts);
        return f;
    }

    private static RewindTotals Totals(FakeCodePatcher f) => new(f, Ids, Bag, Roster, Offsets.RosterSlots, Stride);

    private static void Hand(FakeCodePatcher f, int slot, int id)
        => f.Seed(Roster + slot * Stride + Offsets.RRHand, (byte)(id & 0xFF), (byte)(id >> 8));

    private static RewindRestoreHook Hook(FakeCodePatcher f, Action<byte[]>? onRewind = null, RewindTotals? totals = null)
        => new(f, onRewind ?? (_ => { }), totals ?? Totals(f));

    [Fact]
    public void ShouldArm_requires_each_live_prologue()
    {
        Assert.Equal(16, RewindRestoreHook.ExpectedRestorePrologue.Length);
        Assert.Equal(27, RewindRestoreHook.ExpectedCheckpointPrologue.Length);
        Assert.Equal(SaveEdgeHooks.ApplyBPrologue, RewindRestoreHook.ExpectedCheckpointPrologue.Take(SaveEdgeHooks.ApplyBPrologue.Length));
        foreach (var expected in new[] { RewindRestoreHook.ExpectedRestorePrologue, RewindRestoreHook.ExpectedCheckpointPrologue })
        {
            Assert.True(RewindRestoreHook.ShouldArm(true, (byte[])expected.Clone(), expected));
            Assert.False(RewindRestoreHook.ShouldArm(false, (byte[])expected.Clone(), expected));
            var wrong = (byte[])expected.Clone();
            wrong[5] ^= 0xFF;
            Assert.False(RewindRestoreHook.ShouldArm(true, wrong, expected));
        }
    }

    [Fact]
    public void Checkpoint_restore_puts_the_extended_counts_back_after_the_game_overwrote_them()
    {
        var f = Bagged(2, 0, 3, 1, 0, 0, 0);
        Hand(f, 0, 264);   // one of the 264 is in a hand, so the total is 2
        var totals = Totals(f);
        var hook = Hook(f, totals: totals);
        using var cap = LogCapture.Start();

        nint saved = hook.ProcessCheckpoint((_, _, _, _) => 7, 0x5000, 0, 0, 0);
        Assert.Equal((nint)7, saved);
        Assert.Equal("checkpoint", totals.LatestSource);

        // The game's 0x105 byte copy leaves the extended ids at zero, the hand still holds the weapon.
        hook.ProcessCheckpoint((_, _, _, _) =>
        {
            f.TryWrite(Bag + 261, new byte[7]);
            return 0;
        }, 0x5000, 1, 0, 0);

        Assert.Equal(new byte[] { 2, 0, 3, 1, 0, 0, 0 }, f.Read(Bag + 261, 7));
        Assert.Equal(1, hook.CheckpointRestores);
        Assert.Single(cap.File, l => l.Contains("put its checkpoint back"));
    }

    [Fact]
    public void Checkpoint_restore_counts_the_hands_the_roster_holds_now()
    {
        var f = Bagged(0, 0, 0, 4, 0, 0, 0);
        Hand(f, 2, 264);
        var hook = Hook(f);

        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 0, 0, 0);   // total 5
        // The game reverts the roster hand and zeroes the extended counts.
        hook.ProcessCheckpoint((_, _, _, _) =>
        {
            Hand(f, 2, 0);
            f.TryWrite(Bag + 261, new byte[7]);
            return 0;
        }, 0x5000, 1, 0, 0);

        Assert.Equal(new byte[] { 0, 0, 0, 5, 0, 0, 0 }, f.Read(Bag + 261, 7));
    }

    [Fact]
    public void Checkpoint_restore_makes_the_shadow_total_the_latest()
    {
        var f = Bagged(0, 0, 0, 4, 0, 0, 0);
        Hand(f, 0, 264);
        var totals = Totals(f);
        var hook = Hook(f, totals: totals);

        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 0, 0, 0);   // total 5
        f.TryWrite(Bag + 264, new byte[] { 6 });
        totals.Capture("save");   // a mid battle save moves Latest to 7
        hook.ProcessCheckpoint((_, _, _, _) =>
        {
            f.TryWrite(Bag + 261, new byte[7]);
            return 0;
        }, 0x5000, 1, 0, 0);

        Assert.Equal(new byte[] { 4 }, f.Read(Bag + 264, 1));
        Assert.Equal("checkpoint restore", totals.LatestSource);
        Assert.Equal(5, totals.Latest![264]);
    }

    [Fact]
    public void Checkpoint_restore_without_a_record_writes_nothing_and_warns_once()
    {
        var f = Bagged(2, 0, 3, 1, 0, 0, 0);
        var hook = Hook(f);
        using var cap = LogCapture.Start();

        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 1, 0, 0);
        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 1, 0, 0);

        Assert.Empty(f.Writes);
        Assert.Equal(2, hook.CheckpointRestoresUnrecorded);
        Assert.Equal(0, hook.CheckpointRestores);
        Assert.Single(cap.File, l => l.Contains("never saw being saved"));
    }

    [Fact]
    public void Checkpoint_save_with_an_unreadable_roster_row_files_nothing_and_the_restore_warns()
    {
        var f = Bagged(2, 0, 3, 1, 0, 0, 0);
        f.RefuseReadsAt.Add(Roster + 9 * Stride + Offsets.RRHand);
        var hook = Hook(f);
        using var cap = LogCapture.Start();

        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 0, 0, 0);

        Assert.Equal(0, hook.CheckpointSaves);
        Assert.Equal(1, hook.CheckpointSavesIncomplete);
        Assert.Equal(0, hook.TotalsByStruct.Count);

        hook.ProcessCheckpoint((_, _, _, _) =>
        {
            f.TryWrite(Bag + 261, new byte[7]);
            return 0;
        }, 0x5000, 1, 0, 0);

        Assert.Equal(new byte[7], f.Read(Bag + 261, 7));
        Assert.Equal(1, hook.CheckpointRestoresUnrecorded);
        Assert.Single(cap.File, l => l.Contains("never saw being saved"));
    }

    [Fact]
    public void Checkpoint_other_modes_pass_through()
    {
        var f = Bagged(2, 0, 3, 1, 0, 0, 0);
        var hook = Hook(f);
        using var cap = LogCapture.Start();
        int calls = 0;

        hook.ProcessCheckpoint((_, _, _, _) => { calls++; return 0; }, 0x5000, 2, 0, 0);
        hook.ProcessCheckpoint((_, _, _, _) => { calls++; return 0; }, 0x5000, -1, 0, 0);

        Assert.Equal(2, calls);
        Assert.Equal(0, hook.TotalsByStruct.Count);
        Assert.Empty(f.Writes);
        Assert.Empty(cap.File);
    }

    [Fact]
    public void Shadow_keeps_the_newest_eight_structs()
    {
        var shadow = new CheckpointTotalShadow();
        for (long k = 1; k <= 9; k++) shadow.RememberForStruct(k, new Dictionary<int, int> { [261] = (int)k });

        Assert.Equal(8, shadow.Count);
        Assert.False(shadow.TryGetForStruct(1, out _));
        for (long k = 2; k <= 9; k++) Assert.True(shadow.TryGetForStruct(k, out _));

        shadow.RememberForStruct(3, new Dictionary<int, int> { [261] = 99 });
        Assert.True(shadow.TryGetForStruct(3, out var counts));
        Assert.Equal(99, counts[261]);
        Assert.Equal(8, shadow.Count);

        shadow.RememberForStruct(10, new Dictionary<int, int>());   // key 3 is now newest, so key 2 is the one evicted
        Assert.False(shadow.TryGetForStruct(2, out _));
        Assert.True(shadow.TryGetForStruct(3, out _));
    }

    [Fact]
    public void Rewind_reads_the_snapshot_header_after_the_original_and_hands_it_on()
    {
        var f = new FakeCodePatcher();
        var seed = Enumerable.Range(0, Offsets.SaveHeaderKeyLen).Select(i => (byte)(i + 1)).ToArray();
        f.Seed(Offsets.SnapshotStructCopy + Offsets.SaveHeaderKeyOff, seed);
        bool originalRan = false;
        byte[]? handed = null;
        var hook = Hook(f, h => { Assert.True(originalRan); handed = h; });

        nint ret = hook.ProcessRewind((_, _, _, _) => { originalRan = true; return 5; }, 0, 0, 0, 0);

        Assert.Equal((nint)5, ret);
        Assert.Equal(seed, handed);
        Assert.Equal(1, hook.RewindsHandedOn);
        Assert.Equal(0, hook.RewindsUnread);
    }

    [Fact]
    public void Rewind_with_an_unreadable_header_calls_nothing_and_warns_once()
    {
        var hook = Hook(new FakeCodePatcher(), _ => throw new InvalidOperationException("must not be called"));
        using var cap = LogCapture.Start();
        int calls = 0;

        hook.ProcessRewind((_, _, _, _) => { calls++; return 0; }, 0, 0, 0, 0);
        hook.ProcessRewind((_, _, _, _) => { calls++; return 0; }, 0, 0, 0, 0);

        Assert.Equal(2, calls);
        Assert.Equal(2, hook.RewindsUnread);
        Assert.Equal(0, hook.RewindsHandedOn);
        Assert.Single(cap.File, l => l.Contains("header could not be read"));
    }

    [Fact]
    public void Install_refuses_a_wrong_prologue_before_any_hook_is_made()
    {
        var f = new FakeCodePatcher();
        f.Seed(0x2000, RewindRestoreHook.ExpectedRestorePrologue);
        f.Seed(0x3000, new byte[16]);
        var hook = new RewindRestoreHook(f, _ => { }, Totals(f), restoreAddr: 0x2000, checkpointAddr: 0x3000);

        // null hooks: a refusal that touched the hooks controller would throw instead of returning text
        string? why = hook.Install(null!);

        Assert.NotNull(why);
        Assert.StartsWith("checkpoint-copy: 0x3000 does not carry the expected prologue", why);
        Assert.False(hook.IsActive);
    }

    [Fact]
    public void Checkpoint_restores_seen_counts_mode_1_with_or_without_a_record_and_nothing_else()
    {
        var f = Bagged(2, 0, 3, 1, 0, 0, 0);
        var hook = Hook(f);
        using var cap = LogCapture.Start();

        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 0, 0, 0);
        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 2, 0, 0);
        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, -1, 0, 0);
        Assert.Equal(0, hook.CheckpointRestoresSeen);

        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x5000, 1, 0, 0);
        hook.ProcessCheckpoint((_, _, _, _) => 0, 0x9000, 1, 0, 0);
        Assert.Equal(2, hook.CheckpointRestoresSeen);
    }

    private static FakeCodePatcher Prologued()
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.Seed(Offsets.FnSnapshotRestore, RewindRestoreHook.ExpectedRestorePrologue);
        f.Seed(Offsets.FnCheckpointCopy, RewindRestoreHook.ExpectedCheckpointPrologue);
        return f;
    }

    [Fact]
    public void Install_survives_detours_firing_during_activate()
    {
        var hook = Hook(Prologued());
        var fh = FiringHooks.Make(out var hooks);

        Assert.Null(hook.Install(hooks));

        Assert.All(fh.Created, h => Assert.True(h.Fired));
        Assert.Equal(2, fh.Created.Count);
        Assert.True(hook.IsActive);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Install_failing_at_an_activation_stores_only_the_hooks_that_went_live(int k)
    {
        var hook = Hook(Prologued());
        var fh = FiringHooks.Make(out var hooks);
        fh.ThrowOnActivation = k;
        fh.EnabledWhenThrowing = true;

        Assert.NotNull(hook.Install(hooks));
        Assert.False(hook.IsActive);
        Assert.Equal(k - 1, hook.StoredHooks);
        Assert.Equal(2, fh.Created.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(i < k - 1, fh.Created[i].Activated);
            Assert.False(fh.Created[i].IsHookEnabled);
            Assert.Equal(i <= k - 1 ? 1 : 0, fh.Created[i].DisableCalls);
        }

        fh.ThrowOnActivation = 0;
        string? second = hook.Install(hooks);
        if (k > 1)
        {
            Assert.Equal("rewind-restore: an earlier install failed partway, restart the game to retry", second);
            Assert.Equal(2, fh.Created.Count);
            Assert.False(hook.IsActive);
        }
        else
        {
            Assert.Null(second);
            Assert.Equal(4, fh.Created.Count);
            Assert.True(hook.IsActive);
        }
    }

    [Fact]
    public void Install_after_a_release_switches_the_same_hooks_back_on()
    {
        var hook = Hook(Prologued());
        var fh = FiringHooks.Make(out var hooks);
        fh.Fire = false;
        Assert.Null(hook.Install(hooks));
        hook.Release();
        Assert.False(hook.IsActive);

        Assert.Null(hook.Install(hooks));

        Assert.True(hook.IsActive);
        Assert.Equal(2, fh.Created.Count);
        Assert.All(fh.Created, h => Assert.True(h.IsHookEnabled));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Install_failing_at_a_create_activates_nothing_and_a_retry_builds_both(int c)
    {
        var hook = Hook(Prologued());
        var fh = FiringHooks.Make(out var hooks);
        fh.Fire = false;
        fh.ThrowOnCreate = c;

        string? first = hook.Install(hooks);

        Assert.Contains("rewind-restore: hook install failed (create refused)", first);
        Assert.False(hook.IsActive);
        Assert.Equal(0, hook.StoredHooks);
        Assert.Equal(c - 1, fh.Created.Count);
        Assert.All(fh.Created, h => Assert.False(h.Activated));

        fh.ThrowOnCreate = 0;
        Assert.Null(hook.Install(hooks));
        Assert.True(hook.IsActive);
        Assert.Equal(c - 1 + 2, fh.Created.Count);
        Assert.All(fh.Created.Skip(c - 1), h => Assert.True(h.Activated));
    }
}
