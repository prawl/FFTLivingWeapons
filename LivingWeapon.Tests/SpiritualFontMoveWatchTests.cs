using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// MoveWatch state machine -- the pure position-poll trigger for "Spiritual Font".
/// Lives in SpiritualFont.Policy.cs (SpiritualFont.MoveWatch nested class).
///
/// Contract:
///   • First sighting / after reset: baseline silently, never fires.
///   • New tile: must be stable for StabilityTicks=3 consecutive ticks before firing.
///     Mid-animation flicker and documented (0,0)-band-position pulses are filtered.
///   • After firing: rate-capped for RateCap=90 ticks (~3 s).
///   • Return to the current baseline after fire: fires again on the next stable change.
///   • Reset: back to Fresh state; next observe is a silent baseline.
/// Move-only turns now pay (the old actor-latch gap is closed).
/// </summary>
public class SpiritualFontMoveWatchTests
{
    private static SpiritualFont.MoveWatch W() => new SpiritualFont.MoveWatch();

    [Fact]
    public void First_sighting_baselines_silently()
    {
        var w = W();
        Assert.True(w.IsFresh);
        Assert.False(w.Observe(5, 5));
        Assert.True(w.IsStable);
    }

    [Fact]
    public void Standing_still_never_fires()
    {
        var w = W();
        w.Observe(3, 3);
        for (int i = 0; i < 10; i++)
            Assert.False(w.Observe(3, 3));
    }

    [Fact]
    public void New_tile_requires_stability_before_firing()
    {
        var w = W();
        w.Observe(3, 3);
        Assert.False(w.Observe(5, 7));
        Assert.False(w.Observe(5, 7));
        Assert.True(w.IsCandidate);
        Assert.Equal(2, w.StabCount);
        Assert.True(w.Observe(5, 7));   // third tick fires
        Assert.True(w.IsCooldown);
    }

    [Fact]
    public void Flickering_position_does_not_fire()
    {
        var w = W();
        w.Observe(3, 3);
        for (int i = 0; i < 20; i++)
            Assert.False(w.Observe(i % 2 == 0 ? 5 : 3, 3));
    }

    [Fact]
    public void Flicker_to_zero_zero_does_not_fire()
    {
        // Band position pulses to (0,0) transiently mid-animation.
        var w = W();
        w.Observe(4, 4);
        Assert.False(w.Observe(0, 0));
        Assert.False(w.Observe(4, 4));
        Assert.False(w.Observe(0, 0));
        Assert.False(w.Observe(4, 4));
        Assert.False(w.IsCandidate);
    }

    [Fact]
    public void Stable_change_fires_exactly_once()
    {
        var w = W();
        w.Observe(3, 3);
        w.Observe(6, 6);
        w.Observe(6, 6);
        Assert.True(w.Observe(6, 6));
        Assert.False(w.Observe(6, 6));
        Assert.False(w.Observe(6, 6));
    }

    [Fact]
    public void Rate_cap_suppresses_immediate_second_fire()
    {
        var w = W();
        w.Observe(0, 0);
        for (int i = 0; i < SpiritualFont.StabilityTicks - 1; i++) w.Observe(1, 1);
        Assert.True(w.Observe(1, 1));   // fire; enter cooldown
        // New position while cooldown active
        w.Observe(2, 2);
        w.Observe(2, 2);
        Assert.False(w.Observe(2, 2));
        Assert.True(w.IsCooldown);
    }

    [Fact]
    public void Rate_cap_expires_and_next_move_fires()
    {
        var w = W();
        w.Observe(0, 0);
        for (int i = 0; i < SpiritualFont.StabilityTicks - 1; i++) w.Observe(1, 1);
        Assert.True(w.Observe(1, 1));
        for (int i = 0; i < SpiritualFont.RateCap; i++) w.Observe(1, 1);
        Assert.True(w.IsStable);
        for (int i = 0; i < SpiritualFont.StabilityTicks - 1; i++) w.Observe(3, 3);
        Assert.True(w.Observe(3, 3));
    }

    [Fact]
    public void Return_to_original_tile_after_fire_fires_again()
    {
        var w = W();
        w.Observe(2, 2);
        for (int i = 0; i < SpiritualFont.StabilityTicks - 1; i++) w.Observe(5, 5);
        Assert.True(w.Observe(5, 5));
        for (int i = 0; i < SpiritualFont.RateCap; i++) w.Observe(5, 5);
        // Baseline is now (5,5); return to (2,2) is a new stable move
        for (int i = 0; i < SpiritualFont.StabilityTicks - 1; i++) w.Observe(2, 2);
        Assert.True(w.Observe(2, 2));
    }

    [Fact]
    public void Reset_clears_all_state()
    {
        var w = W();
        w.Observe(2, 2);
        w.Observe(5, 5);
        w.Observe(5, 5);   // candidate in progress
        w.Reset();
        Assert.True(w.IsFresh);
        Assert.False(w.Observe(9, 9));   // silent baseline after reset
        Assert.True(w.IsStable);
    }

    [Fact]
    public void Candidate_resets_when_different_new_tile_appears_mid_count()
    {
        var w = W();
        w.Observe(0, 0);
        w.Observe(1, 1);   // start toward (1,1)
        w.Observe(1, 1);   // stabCount = 2
        w.Observe(2, 2);   // different tile: restart
        Assert.Equal(1, w.StabCount);
        Assert.True(w.IsCandidate);
        w.Observe(2, 2);
        Assert.True(w.Observe(2, 2));   // fires on (2,2)
    }

    [Fact]
    public void Constants_are_correct()
    {
        Assert.Equal(3, SpiritualFont.StabilityTicks);
        Assert.Equal(90, SpiritualFont.RateCap);
    }
}
