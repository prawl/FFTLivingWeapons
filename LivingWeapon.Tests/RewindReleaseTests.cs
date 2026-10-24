using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

// These tests check that a rewound battle frees the Provoke hold and the Bulwark, and says so once.
// The release of each half is tested where it lives, in the ReleaseForRewind tests of ProvokeHoldTests
// and BulwarkTests. This file covers the trigger, the order of the release action and the announcement,
// using recording lambdas because Engine is too heavy to build here.
public class RewindReleaseTests
{
    [Theory]
    [InlineData(-1, 0, false)]
    [InlineData(-1, 5, false)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 2, false)]
    public void ShouldRelease_fires_only_on_a_rise_after_priming(long lastSeen, long now, bool expected)
        => Assert.Equal(expected, RewindRelease.ShouldRelease(lastSeen, now));

    [Fact]
    public void Tick_primes_on_first_reading_fires_once_on_a_rise_and_stays_quiet_when_steady()
    {
        long count = 4;
        int fired = 0;
        var gate = new RewindRelease(() => count, () => fired++);

        gate.Tick();
        Assert.Equal(0, fired);   // an inherited count only primes
        gate.Tick();
        Assert.Equal(0, fired);

        count = 5;
        gate.Tick();
        Assert.Equal(1, fired);
        gate.Tick();
        gate.Tick();
        Assert.Equal(1, fired);

        count = 6;
        gate.Tick();
        Assert.Equal(2, fired);
    }

    private static (Action Run, List<string> Calls, List<string> Logs, List<string> Records) Build(bool provoke, bool bulwark)
    {
        var calls = new List<string>();
        var logs = new List<string>();
        var records = new List<string>();
        var run = RewindRelease.Sequence(
            () => provoke, () => bulwark,
            () => calls.Add("provoke"), () => calls.Add("bulwark"), () => calls.Add("toasts"),
            logs.Add, records.Add);
        return (run, calls, logs, records);
    }

    [Fact]
    public void Sequence_runs_provoke_then_bulwark_then_toasts()
    {
        var (run, calls, _, _) = Build(true, true);

        Assert.Empty(calls);
        run();

        Assert.Equal(new[] { "provoke", "bulwark", "toasts" }, calls);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 1)]
    public void Sequence_logs_one_event_only_when_something_was_held_and_always_records(bool provoke, bool bulwark, int events)
    {
        var (run, _, logs, records) = Build(provoke, bulwark);

        run();

        Assert.Equal(events, logs.Count);
        if (events == 1) Assert.Equal(RewindRelease.Line(provoke, bulwark), logs[0]);
        Assert.Equal(new[] { $"release provoke={provoke} bulwark={bulwark}" }, records);
    }

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, "The battle was rewound, so the Provoke hold lets go and waits for the rewound battle to set it up again.")]
    [InlineData(false, true, "The battle was rewound, so the Sunderer's Bulwark lets go and waits for the rewound battle to set it up again.")]
    [InlineData(true, true, "The battle was rewound, so the Provoke hold and the Sunderer's Bulwark let go and wait for the rewound battle to set them up again.")]
    public void Line_names_only_what_was_held(bool provoke, bool bulwark, string? expected)
        => Assert.Equal(expected, RewindRelease.Line(provoke, bulwark));

    [Fact]
    public void Sequence_reads_what_was_held_before_it_releases()
    {
        bool held = true;
        var logs = new List<string>();
        var run = RewindRelease.Sequence(() => held, () => false, () => held = false, () => { }, () => { }, logs.Add, _ => { });

        run();

        Assert.Single(logs);
    }
}
