using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Rapture's window clock and early release. The original expiry rode TurnTracker's global
/// acted-edge attribution, which mis-credits under the cursor-following active struct -- live,
/// the window never aged and the teleport never expired. The fix counts the wielder's COMPLETED
/// turns off their OWN scheduler CT (band +0x25, CharmLock's >=90-then-&lt;70 discipline), and
/// releases early the moment HP recovers to/above the threshold.
/// </summary>
public class RaptureWindowTests
{
    // ---- CtTurns: completed-turn counting off the wielder's own CT ----

    [Fact]
    public void CtTurns_counts_a_turn_only_after_rise_then_fall()
    {
        var t = new CtTurns();
        t.Observe(50);
        Assert.Equal(0, t.Completed);
        t.Observe(95);                  // turn arrives
        t.Observe(96);
        Assert.Equal(0, t.Completed);   // still their turn
        t.Observe(60);                  // turn taken
        Assert.Equal(1, t.Completed);
    }

    [Fact]
    public void CtTurns_ignores_a_fall_without_a_rise()
    {
        var t = new CtTurns();
        t.Observe(60);
        t.Observe(40);
        t.Observe(0);
        Assert.Equal(0, t.Completed);
    }

    [Fact]
    public void CtTurns_mid_band_values_are_neither_rise_nor_fall()
    {
        var t = new CtTurns();
        t.Observe(95);
        t.Observe(80);                  // between TurnLo and TurnHi: still ambiguous
        Assert.Equal(0, t.Completed);
        t.Observe(10);
        Assert.Equal(1, t.Completed);
    }

    [Fact]
    public void CtTurns_counts_each_completed_turn()
    {
        var t = new CtTurns();
        foreach (int ct in new[] { 95, 60, 99, 50, 100, 10 }) t.Observe(ct);
        Assert.Equal(3, t.Completed);
    }

    [Fact]
    public void CtTurns_reset_zeroes_count_and_phase()
    {
        var t = new CtTurns();
        t.Observe(95);
        t.Reset();
        t.Observe(60);                  // the pre-reset rise must not complete a turn
        Assert.Equal(0, t.Completed);
    }

    // ---- HasRecovered: early release when HP comes back above the threshold ----

    [Fact]
    public void HasRecovered_true_at_and_above_the_threshold()
    {
        Assert.True(Rapture.HasRecovered(hp: 30, maxHp: 100, pct: 0.30));   // exactly 30%
        Assert.True(Rapture.HasRecovered(hp: 95, maxHp: 100, pct: 0.30));
    }

    [Fact]
    public void HasRecovered_false_while_still_below()
        => Assert.False(Rapture.HasRecovered(hp: 29, maxHp: 100, pct: 0.30));

    [Fact]
    public void HasRecovered_false_for_a_dead_wielder()
        => Assert.False(Rapture.HasRecovered(hp: 0, maxHp: 100, pct: 0.30));   // death release owns hp==0

    [Fact]
    public void HasRecovered_false_on_junk_max()
        => Assert.False(Rapture.HasRecovered(hp: 10, maxHp: 0, pct: 0.30));
}
