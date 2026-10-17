using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-392: <see cref="LandmarkStall"/> is a decorator over a <see cref="GuardLandmark"/> that
/// counts CONSECUTIVE Unreadable readings and fires a callback exactly once when the streak
/// reaches the threshold. These tests drive it with a scripted inner probe, no game memory.
/// </summary>
public class LandmarkStallTests
{
    private static GuardLandmark Scripted(Queue<LandmarkReading> script, string name = "scripted")
        => new(name, () => script.Dequeue());

    private static Queue<LandmarkReading> Repeat(LandmarkVerdict v, int n)
    {
        var q = new Queue<LandmarkReading>();
        for (int i = 0; i < n; i++) q.Enqueue(v);
        return q;
    }

    [Fact]
    public void S1_fires_exactly_once_when_the_streak_reaches_the_threshold_and_not_after()
    {
        var calls = new List<(string name, string? detail)>();
        var script = Repeat(LandmarkVerdict.Unreadable, 20);
        var wrapped = LandmarkStall.Watch(Scripted(script), 5, (n, d) => calls.Add((n, d)));

        for (int i = 0; i < 4; i++) wrapped.Probe();
        Assert.Empty(calls);
        wrapped.Probe();                 // the 5th consecutive Unreadable
        Assert.Single(calls);
        for (int i = 0; i < 15; i++) wrapped.Probe();   // streak continues: still just the one call
        Assert.Single(calls);
        Assert.Equal("scripted", calls[0].name);
    }

    [Fact]
    public void S2_never_fires_below_the_threshold()
    {
        int fired = 0;
        var wrapped = LandmarkStall.Watch(Scripted(Repeat(LandmarkVerdict.Unreadable, 4)), 5, (_, _) => fired++);

        for (int i = 0; i < 4; i++) wrapped.Probe();

        Assert.Equal(0, fired);
    }

    [Theory]
    [InlineData(true)]    // Match breaks the streak
    [InlineData(false)]   // Mismatch breaks the streak
    public void S3_a_non_unreadable_verdict_resets_the_streak_and_a_fresh_climb_fires_again(bool breakWithMatch)
    {
        var breaker = breakWithMatch ? LandmarkVerdict.Match : LandmarkVerdict.Mismatch;
        int fired = 0;
        var script = new Queue<LandmarkReading>();
        for (int i = 0; i < 3; i++) script.Enqueue(LandmarkVerdict.Unreadable);
        script.Enqueue(breaker);                                   // resets at 3 of 3
        for (int i = 0; i < 3; i++) script.Enqueue(LandmarkVerdict.Unreadable);   // 3 again: must not fire yet (a non-reset counter would hit 6)
        var wrapped = LandmarkStall.Watch(Scripted(script), 4, (_, _) => fired++);

        for (int i = 0; i < 7; i++) wrapped.Probe();
        Assert.Equal(0, fired);

        script.Enqueue(LandmarkVerdict.Unreadable);                // 4th consecutive since the reset
        wrapped.Probe();
        Assert.Equal(1, fired);

        // Break the streak and climb back: fires a second time.
        script.Enqueue(breaker);
        for (int i = 0; i < 4; i++) script.Enqueue(LandmarkVerdict.Unreadable);
        for (int i = 0; i < 5; i++) wrapped.Probe();
        Assert.Equal(2, fired);
    }

    [Fact]
    public void S4_readings_pass_through_unchanged_and_the_name_is_kept()
    {
        var script = new Queue<LandmarkReading>();
        script.Enqueue(new LandmarkReading(LandmarkVerdict.Mismatch, "observed X"));
        script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "slot empty"));
        script.Enqueue(LandmarkVerdict.Match);
        var wrapped = LandmarkStall.Watch(Scripted(script, "ramza"), 10, (_, _) => { });

        Assert.Equal("ramza", wrapped.Name);
        var a = wrapped.Probe();
        Assert.Equal(LandmarkVerdict.Mismatch, a.Verdict);
        Assert.Equal("observed X", a.Detail);
        var b = wrapped.Probe();
        Assert.Equal(LandmarkVerdict.Unreadable, b.Verdict);
        Assert.Equal("slot empty", b.Detail);
        var c = wrapped.Probe();
        Assert.Equal(LandmarkVerdict.Match, c.Verdict);
        Assert.Null(c.Detail);
    }

    [Fact]
    public void S4b_the_callback_receives_the_detail_of_the_reading_that_hit_the_threshold()
    {
        string? seen = null;
        var script = new Queue<LandmarkReading>();
        script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "first"));
        script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "page unreadable"));
        var wrapped = LandmarkStall.Watch(Scripted(script), 2, (_, d) => seen = d);

        wrapped.Probe();
        wrapped.Probe();

        Assert.Equal("page unreadable", seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void S5_a_non_positive_threshold_throws(int steps)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LandmarkStall.Watch(Scripted(new Queue<LandmarkReading>()), steps, (_, _) => { }));
    }

    [Fact]
    public void S6_an_unreadable_the_predicate_excludes_resets_the_streak_and_never_counts()
    {
        // LW-393: the sensing lane's "battle live" and "boot grace" waits are Unreadable by design.
        int fired = 0;
        var script = new Queue<LandmarkReading>();
        for (int i = 0; i < 3; i++) script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "real"));
        for (int i = 0; i < 10; i++) script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "battle live"));
        for (int i = 0; i < 3; i++) script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "real"));
        var wrapped = LandmarkStall.Watch(Scripted(script), 5, (_, _) => fired++, r => r.Detail != "battle live");

        for (int i = 0; i < 16; i++) wrapped.Probe();

        Assert.Equal(0, fired);   // 3 real, a reset, 3 real: never 5 in a row
    }

    [Fact]
    public void S7_without_a_predicate_every_unreadable_counts()
    {
        int fired = 0;
        var script = new Queue<LandmarkReading>();
        for (int i = 0; i < 5; i++) script.Enqueue(new LandmarkReading(LandmarkVerdict.Unreadable, "battle live"));
        var wrapped = LandmarkStall.Watch(Scripted(script), 5, (_, _) => fired++);

        for (int i = 0; i < 5; i++) wrapped.Probe();

        Assert.Equal(1, fired);
    }
}
