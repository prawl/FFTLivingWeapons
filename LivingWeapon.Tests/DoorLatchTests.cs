using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-399: the writer latch every table writer asks before its first write.</summary>
public class DoorLatchTests
{
    private const string Refusal = "The thing was not written; its data is not where this mod expects it.";

    private static DoorLatch Latch(Func<DoorReading> door) => new(door, Refusal);

    [Fact]
    public void An_open_door_allows()
    {
        var latch = Latch(DoorReading.Open);
        Assert.True(latch.Allows());
    }

    [Fact]
    public void A_closed_door_refuses_and_warns_exactly_once_across_many_calls()
    {
        using var cap = LogCapture.Start();
        var latch = Latch(() => DoorReading.Failed("gone"));
        for (int i = 0; i < 50; i++) Assert.False(latch.Allows());
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains(Refusal, warn);
        Assert.DoesNotContain("standing down to protect your save", warn);
    }

    [Fact]
    public void A_waiting_door_refuses_without_a_warning_and_with_one_debug_line()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var latch = Latch(() => DoorReading.Waiting("not yet"));
        for (int i = 0; i < 20; i++) Assert.False(latch.Allows());
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));
        Assert.Single(cap.File, l => l.Contains("[DEBUG]") && l.Contains("not yet"));
    }

    [Fact]
    public void A_door_that_goes_from_waiting_to_open_starts_allowing()
    {
        var reading = DoorReading.Waiting("not yet");
        var latch = Latch(() => reading);
        Assert.False(latch.Allows());
        reading = DoorReading.Open();
        Assert.True(latch.Allows());
    }

    [Fact]
    public void A_door_that_goes_from_open_to_closed_refuses_on_the_next_call()
    {
        using var cap = LogCapture.Start();
        var reading = DoorReading.Open();
        var latch = Latch(() => reading);
        Assert.True(latch.Allows());
        reading = DoorReading.Failed("gone");
        Assert.False(latch.Allows());
        Assert.Single(cap.File, l => l.Contains("[WARN]"));
    }

    [Fact]
    public void An_absent_door_refuses_like_a_closed_one()
    {
        using var cap = LogCapture.Start();
        var latch = Latch(() => DoorReading.Absent("nothing shipped"));
        Assert.False(latch.Allows());
    }
}
