using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-452 round two: the game re derives its own bag counts from the roster on the retry roads, so the
/// extended counts are kept as total owned (bag plus hands) and the bag is set to total minus hands after a rewind.</summary>
public class RewindTotalsTests
{
    private const long Bag = 0x1000;
    private const long Roster = 0x20000;
    private const int Stride = 0x258;
    private static readonly int[] Ids = { 261, 262, 263, 264, 265, 266, 267 };

    private static RewindTotals Totals(FakeCodePatcher f)
        => new(f, Ids, Bag, Roster, Offsets.RosterSlots, Stride, id => id.ToString());

    private static void Hand(FakeCodePatcher f, int slot, int offset, int id)
        => f.Seed(Roster + slot * Stride + offset, (byte)(id & 0xFF), (byte)(id >> 8));

    private static FakeCodePatcher World(int bag264)
    {
        var f = new FakeCodePatcher { ZeroFillUnseeded = true };
        f.Seed(Bag + 264, (byte)bag264);
        return f;
    }

    [Fact]
    public void ReadHands_counts_right_and_off_hands_across_the_roster()
    {
        var f = World(0);
        Hand(f, 0, Offsets.RRHand, 264);
        Hand(f, 3, Offsets.ROffHand, 264);
        Hand(f, 7, Offsets.RRHand, 262);

        var hands = Totals(f).ReadHands()!;

        Assert.Equal(2, hands[264]);
        Assert.Equal(1, hands[262]);
        Assert.Equal(0, hands[261]);
    }

    [Fact]
    public void Capture_records_bag_plus_hands()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);

        totals.Capture("checkpoint");

        Assert.Equal(5, totals.Latest![264]);
        Assert.Equal("checkpoint", totals.LatestSource);
        Assert.Equal(1, totals.Captures);
    }

    [Fact]
    public void Recompute_puts_the_bag_at_total_minus_hands_after_the_roster_reverted()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");

        // The game put the Chaos Blade back, so the hand is empty while the bag still reads 4.
        Hand(f, 2, Offsets.RRHand, 0);

        Assert.True(totals.Recompute("test", out string change));
        Assert.Equal(new byte[] { 5 }, f.Read(Bag + 264, 1));
        Assert.Contains("264 4 to 5", change);
        Assert.Equal(1, totals.Corrections);
    }

    [Fact]
    public void Recompute_writes_nothing_when_the_bag_already_matches()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("save");
        f.Writes.Clear();

        Assert.False(totals.Recompute("test", out _));
        Assert.Empty(f.Writes);
        Assert.Equal(0, totals.Corrections);
        Assert.Equal(1, totals.Recomputes);
    }

    [Fact]
    public void Recompute_writes_nothing_when_a_roster_row_is_unreadable()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        Hand(f, 2, Offsets.RRHand, 0);
        f.RefuseReadsAt.Add(Roster + 9 * Stride + Offsets.RRHand);
        f.Writes.Clear();

        Assert.False(totals.Recompute("test", out string change));
        Assert.Equal(new byte[] { 4 }, f.Read(Bag + 264, 1));
        Assert.Empty(f.Writes);
        Assert.Contains("read failed", change);
        Assert.Equal(1, totals.Incomplete);
        Assert.Equal(0, totals.Corrections);
    }

    [Fact]
    public void Capture_keeps_the_previous_total_when_a_read_fails()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        f.RefuseReadsAt.Add(Bag + 264);

        totals.Capture("save");

        Assert.Equal(5, totals.Latest![264]);
        Assert.Equal("checkpoint", totals.LatestSource);
        Assert.Equal(1, totals.Incomplete);
    }

    [Fact]
    public void PutBack_adopts_the_totals_even_when_the_hands_read_fails()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        f.RefuseReadsAt.Add(Roster + 9 * Stride + Offsets.RRHand);
        f.Writes.Clear();

        var wrote = totals.PutBack(new Dictionary<int, int> { [264] = 9 }, "checkpoint restore");

        Assert.Empty(wrote);
        Assert.Empty(f.Writes);
        Assert.Equal(1, totals.Incomplete);
        Assert.Equal(9, totals.Latest![264]);
        Assert.Equal("checkpoint restore", totals.LatestSource);
    }

    [Fact]
    public void Tick_step_records_an_incomplete_pass()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        long restores = 0;
        var recorded = new List<string>();
        var step = new RewindRecomputeStep(totals, (_, change) => recorded.Add(change), ("checkpoint restore", () => restores));
        Hand(f, 2, Offsets.RRHand, 0);
        f.RefuseReadsAt.Add(Roster + 9 * Stride + Offsets.RRHand);
        f.Writes.Clear();
        using var cap = LogCapture.Start();

        step.Step(1);
        restores++;
        step.Step(2);
        step.Step(5);

        Assert.Single(recorded);
        Assert.Contains("read failed", recorded[0]);
        Assert.Empty(f.Writes);
        Assert.DoesNotContain(cap.File, l => l.Contains("re derived"));
    }

    [Fact]
    public void Recompute_without_a_capture_does_nothing()
    {
        var f = World(4);
        Assert.False(Totals(f).Recompute("test", out _));
        Assert.Empty(f.Writes);
    }

    [Fact]
    public void Recompute_never_goes_below_zero()
    {
        var f = World(0);
        Hand(f, 1, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("save");   // total 1
        Hand(f, 1, Offsets.ROffHand, 264);   // two hands now hold it
        f.Seed(Bag + 264, 3);

        Assert.True(totals.Recompute("test", out _));
        Assert.Equal(new byte[] { 0 }, f.Read(Bag + 264, 1));
    }

    [Fact]
    public void A_refused_write_is_counted_and_named()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("save");
        Hand(f, 2, Offsets.RRHand, 0);
        f.RefuseWritesAt.Add(Bag + 264);

        Assert.False(totals.Recompute("test", out string change));
        Assert.Equal(1, totals.Refused);
        Assert.Contains("264", change);
    }

    [Fact]
    public void Tick_step_schedules_a_recompute_when_a_restore_counter_rises()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        long restores = 0;
        var step = new RewindRecomputeStep(totals, null, ("checkpoint restore", () => restores));
        using var cap = LogCapture.Start();

        step.Step(1);
        Hand(f, 2, Offsets.RRHand, 0);   // the game reverts the roster
        step.Step(2);
        Assert.Empty(cap.File);   // nothing rose, nothing scheduled

        restores++;
        step.Step(3);
        Assert.Equal(new byte[] { 4 }, f.Read(Bag + 264, 1));   // the first pass waits
        step.Step(5);
        Assert.Equal(new byte[] { 4 }, f.Read(Bag + 264, 1));
        step.Step(6);

        Assert.Equal(new byte[] { 5 }, f.Read(Bag + 264, 1));
        Assert.Single(cap.File, l => l.Contains("re derived from what the party holds") && l.Contains("264 4 to 5"));
        Assert.Equal("checkpoint restore", totals.LastReason);

        step.Step(18);   // the second pass finds nothing left to fix and says nothing
        Assert.Single(cap.File, l => l.Contains("re derived"));
    }

    [Fact]
    public void Tick_step_ignores_the_inventory_reset_counter_alone()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("load");
        long resets = 0, restores = 0, flees = 0;
        var step = new RewindRecomputeStep(totals, null, ("checkpoint restore", () => restores), ("flee rewind", () => flees));
        Hand(f, 2, Offsets.RRHand, 0);
        using var cap = LogCapture.Start();

        resets++;   // the game's reset also runs at loads and after real battles, and nothing here reads it
        for (long t = 1; t <= 20; t++) step.Step(t);

        Assert.Equal(1, resets);
        Assert.Equal(new byte[] { 4 }, f.Read(Bag + 264, 1));
        Assert.Equal(0, totals.Recomputes);
        Assert.Empty(cap.File);
    }

    [Fact]
    public void Tick_step_warns_once_per_pass_when_a_write_is_refused()
    {
        var f = World(4);
        Hand(f, 2, Offsets.RRHand, 264);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        long restores = 0;
        var step = new RewindRecomputeStep(totals, null, ("checkpoint restore", () => restores));
        Hand(f, 2, Offsets.RRHand, 0);
        f.RefuseWritesAt.Add(Bag + 264);
        using var cap = LogCapture.Start();

        step.Step(1);
        restores++;
        step.Step(2);
        step.Step(5);

        Assert.Single(cap.File, l => l.Contains("could not be re derived after the rewind (write refused)"));
        step.Step(17);
        Assert.Equal(2, cap.File.Count(l => l.Contains("could not be re derived")));
    }

    [Fact]
    public void Tick_step_records_one_flight_line_per_pass_even_when_one_write_is_refused_and_another_lands()
    {
        var f = World(4);
        f.Seed(Bag + 265, 2);
        Hand(f, 2, Offsets.RRHand, 264);
        Hand(f, 3, Offsets.RRHand, 265);
        var totals = Totals(f);
        totals.Capture("checkpoint");
        long restores = 0;
        var recorded = new List<string>();
        var step = new RewindRecomputeStep(totals, (_, change) => recorded.Add(change), ("checkpoint restore", () => restores));
        Hand(f, 2, Offsets.RRHand, 0);
        Hand(f, 3, Offsets.RRHand, 0);
        f.RefuseWritesAt.Add(Bag + 264);

        step.Step(1);
        restores++;
        step.Step(2);
        step.Step(5);

        Assert.Single(recorded);
        Assert.Contains("write refused", recorded[0]);
        Assert.Contains("2 to 3", recorded[0]);
    }

    [Fact]
    public void Scheduler_fires_twice_after_a_schedule_and_resets_on_a_new_one()
    {
        var s = new RecomputeScheduler();
        s.Schedule(100);
        Assert.Equal(2, s.Pending);
        Assert.False(s.Due(102));
        Assert.True(s.Due(103));
        Assert.False(s.Due(104));
        Assert.True(s.Due(115));
        Assert.False(s.Due(116));
        Assert.Equal(0, s.Pending);

        s.Schedule(200);
        s.Schedule(201);
        var fired = new List<long>();
        for (long t = 200; t <= 220; t++) if (s.Due(t)) fired.Add(t);
        Assert.Equal(new long[] { 204, 216 }, fired);
    }
}
