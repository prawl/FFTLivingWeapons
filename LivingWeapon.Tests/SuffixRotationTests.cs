using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Direct tests for the per-ID suffix coverage cycle. The same policy is pinned end-to-end
/// through DisplayRotationTests (FakeHeap, full Tick loop); these pin the unit contract:
/// targets excluded, at most RotationSlice per call, covered ids skipped, and the
/// release-and-recycle round when a chunk's id set is exhausted (the live "bows never
/// suffix-painted" fix -- a small chunk must not be able to starve another chunk's tail ids).
/// </summary>
public class SuffixRotationTests
{
    private static readonly IReadOnlySet<int> NoTargets = new HashSet<int>();

    [Fact]
    public void Target_ids_are_excluded()
    {
        var rot = new SuffixRotation();
        var targets = new HashSet<int> { 1, 2 };

        var take = rot.Take(new[] { 1, 2, 3 }, targets);

        Assert.Equal(new[] { 3 }, take);
    }

    [Fact]
    public void All_target_ids_yields_empty()
    {
        var rot = new SuffixRotation();
        var targets = new HashSet<int> { 1, 2 };

        Assert.Empty(rot.Take(new[] { 1, 2 }, targets));
    }

    [Fact]
    public void Take_is_capped_at_RotationSlice()
    {
        var rot = new SuffixRotation();
        var ids = Enumerable.Range(100, SuffixRotation.RotationSlice * 3).ToArray();

        var take = rot.Take(ids, NoTargets);

        Assert.Equal(SuffixRotation.RotationSlice, take.Count);
    }

    [Fact]
    public void Covered_ids_are_skipped_until_cycle_completes()
    {
        var rot = new SuffixRotation();
        var ids = Enumerable.Range(100, SuffixRotation.RotationSlice + 3).ToArray();

        var first  = rot.Take(ids, NoTargets);
        var second = rot.Take(ids, NoTargets);

        // The second call must pick exactly the ids the first one did not cover.
        Assert.Equal(3, second.Count);
        Assert.Empty(first.Intersect(second));
        Assert.Equal(ids.Except(first).OrderBy(i => i), second.OrderBy(i => i));
    }

    [Fact]
    public void Exhausted_id_set_releases_and_recycles()
    {
        var rot = new SuffixRotation();
        var ids = new[] { 1, 2, 3 };

        var first = rot.Take(ids, NoTargets);
        Assert.Equal(3, first.Count);

        // Every id covered: the next call must release them and start a fresh round
        // (never return empty forever for a live chunk).
        var second = rot.Take(ids, NoTargets);
        Assert.Equal(3, second.Count);
        Assert.Equal(first.OrderBy(i => i), second.OrderBy(i => i));
    }

    [Fact]
    public void Recycle_round_is_also_capped_at_RotationSlice()
    {
        var rot = new SuffixRotation();
        var ids = Enumerable.Range(100, SuffixRotation.RotationSlice).ToArray();

        var first = rot.Take(ids, NoTargets);
        Assert.Equal(SuffixRotation.RotationSlice, first.Count);

        // All covered; the recycle branch must honor the same per-call cap.
        var second = rot.Take(ids, NoTargets);
        Assert.Equal(SuffixRotation.RotationSlice, second.Count);
    }

    [Fact]
    public void Small_chunk_cannot_reset_another_chunks_coverage()
    {
        // The live bug: a 2-card render buffer rescanned every 250ms reset the shared
        // cursor the 20-card master text was walking, so tail ids never painted.
        // With per-ID coverage, interleaving a small chunk must not re-cover big-chunk ids.
        var rot = new SuffixRotation();
        var big   = Enumerable.Range(100, SuffixRotation.RotationSlice * 2).ToArray();
        var small = new[] { 900, 901 };

        var covered = new HashSet<int>(rot.Take(big, NoTargets));
        // Interleave the small chunk several times (its own cycle releases only ITS ids).
        for (int i = 0; i < 5; i++) rot.Take(small, NoTargets);

        var next = rot.Take(big, NoTargets);
        // The second big-chunk call must pick the UNcovered tail ids, proving the small
        // chunk's recycling did not release the big chunk's coverage.
        Assert.Equal(SuffixRotation.RotationSlice, next.Count);
        Assert.Empty(next.Intersect(covered));
    }

    [Fact]
    public void Fresh_buffer_of_covered_id_waits_at_most_one_cycle()
    {
        var rot = new SuffixRotation();
        var ids = new[] { 1, 2 };

        var first = rot.Take(ids, NoTargets);
        Assert.Contains(1, first);

        // A fresh render buffer offering the already-covered id alone: one call releases
        // and re-takes it -- never starved.
        var again = rot.Take(new[] { 1 }, NoTargets);
        Assert.Equal(new[] { 1 }, again);
    }
}
