using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Eclipsebolt's "Eagle Eye" signature -- the pure decisions behind hastening Doom. While a
/// +3 Eclipsebolt is equipped, an enemy's Doom countdown is forced down to 1 so the mark
/// resolves on its next turn. Two pure jobs (no memory; the band scan + write lives in
/// EagleEye.cs): (1) is the aura active and what countdown does it force to, gated on
/// kill-tier; (2) the idempotent hasten rule -- write only a Doomed enemy still above target,
/// so the engine ticks it 1 -> 0 -> death untouched and an expired/undead Doom is left be.
/// </summary>
public class EagleEyeTests
{
    [Theory]
    [InlineData(true, 3, 1, true)]    // fresh Doom at 3 -> hasten to 1
    [InlineData(true, 2, 1, true)]    // caught at 2 -> still hasten
    [InlineData(true, 1, 1, false)]   // already at target -> leave (idempotent)
    [InlineData(true, 0, 1, false)]   // expiring/expired -> never re-arm (would block the death tick)
    [InlineData(false, 3, 1, false)]  // not Doomed -> ignore
    [InlineData(false, 0, 1, false)]
    public void ShouldHasten_only_when_doomed_and_above_target(bool doomed, int cd, int target, bool expected)
    {
        Assert.Equal(expected, EagleEye.ShouldHasten(doomed, cd, target));
    }

    private static WeaponSignature Doom(int to, int atTier) =>
        new() { DoomCountdownTo = to, AtTier = atTier, Slot = "" };

    [Fact]
    public void AuraTarget_zero_without_a_doom_signature()
    {
        Assert.Equal(0, EagleEye.AuraTarget(null, tier: 3));
        Assert.Equal(0, EagleEye.AuraTarget(new WeaponSignature { DoomCountdownTo = 0 }, tier: 3));
    }

    [Fact]
    public void AuraTarget_gates_on_tier_then_returns_the_forced_countdown()
    {
        Assert.Equal(0, EagleEye.AuraTarget(Doom(1, 3), tier: 2));   // not earned yet
        Assert.Equal(1, EagleEye.AuraTarget(Doom(1, 3), tier: 3));   // earned
        Assert.Equal(1, EagleEye.AuraTarget(Doom(1, 3), tier: 4));   // and stays past it
    }
}
