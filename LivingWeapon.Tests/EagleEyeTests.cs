using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Eclipsebolt's "Eagle Eye" signature -- the pure decisions behind hastening Doom. LW-95: the
/// rule fires only on a Doom RISING EDGE (doomed &amp;&amp; !wasDoomed) that is ATTRIBUTED to the
/// wielder's own action (mirrors Larceny's actingMain + actedByte==1 gate), and only ever writes
/// DOWN toward the target countdown. A pre-existing Doom (no edge) or a foe Doomed by any other
/// source (another weapon's proc, an enemy cast) is left alone. The stateful band scan +
/// per-enemy baseline + attribution gate lives in EagleEye.cs.
/// </summary>
public class EagleEyeTests
{
    [Theory]
    [InlineData(true, false, 3, 1, true, true)]    // fresh Doom edge, attributed to the wielder -> hasten
    [InlineData(true, false, 3, 1, false, false)]  // fresh Doom edge, NOT attributed -> leave (LW-95 shape)
    [InlineData(true, true, 3, 1, true, false)]    // pre-existing Doom, no edge -> leave even though wielder acts
    [InlineData(true, false, 1, 1, true, false)]   // already at target -> leave (idempotent)
    [InlineData(true, false, 0, 1, true, false)]   // expiring/expired -> never re-arm (would block the death tick)
    [InlineData(false, false, 3, 1, true, false)]  // not Doomed -> ignore
    [InlineData(false, true, 3, 1, true, false)]   // Doom expired between sweeps (falling edge) -> ignore
    public void ShouldHasten_only_on_an_attributed_doom_rising_edge(bool doomed, bool wasDoomed, int cd, int target, bool attributed, bool expected)
    {
        Assert.Equal(expected, EagleEye.ShouldHasten(doomed, wasDoomed, cd, target, attributed));
    }

    [Fact]
    public void ShouldHasten_ignores_a_doom_edge_not_attributed_to_the_wielder()
    {
        // LW-95: Mortal Coil (id 8) procced Doom on an enemy while a +3 Eclipsebolt merely sat
        // fielded (no action of its own); Eagle Eye hastened it anyway. It must not.
        Assert.False(EagleEye.ShouldHasten(doomed: true, wasDoomed: false, countdown: 3, target: 1, attributed: false));
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
