using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Afterimage ramp (AfterimagePolicy): turns add stacks (capped), a hit wipes them,
/// healing does not, and an unreadable HP read never fakes a hit. Mirrors how Swiftedge's
/// Speed x WP damage should accelerate as the wielder keeps acting unharmed.
/// </summary>
public class AfterimageTests
{
    private const int Cap = 5;
    private const int PerTurn = 1;

    [Fact]
    public void FirstObservationOnlyBaselines()
    {
        var s = AfterimagePolicy.Step(AfterimageState.Empty, turns: 4, hp: 30, cap: Cap);
        Assert.True(s.Seeded);
        Assert.Equal(0, s.Stacks);          // never ramps on the first sight
        Assert.Equal(4, s.LastTurns);       // baselines whatever the live counter already reads
        Assert.Equal(30, s.LastHp);
    }

    [Fact]
    public void CompletingATurnAddsOneStack()
    {
        var s = Seed(turns: 0, hp: 30);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 30, cap: Cap);
        Assert.Equal(1, s.Stacks);
        s = AfterimagePolicy.Step(s, turns: 2, hp: 30, cap: Cap);
        Assert.Equal(2, s.Stacks);
    }

    [Fact]
    public void StacksAreCapped()
    {
        var s = Seed(turns: 0, hp: 30);
        for (int t = 1; t <= 10; t++) s = AfterimagePolicy.Step(s, turns: t, hp: 30, cap: Cap);
        Assert.Equal(Cap, s.Stacks);
    }

    [Fact]
    public void AMultiTurnJumpAddsThatManyStacks()
    {
        var s = Seed(turns: 0, hp: 30);
        s = AfterimagePolicy.Step(s, turns: 3, hp: 30, cap: Cap);   // missed two intermediate ticks
        Assert.Equal(3, s.Stacks);
    }

    [Fact]
    public void TakingAHitResetsTheRamp()
    {
        var s = Seed(turns: 0, hp: 30);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 30, cap: Cap);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 22, cap: Cap);   // HP dropped: a hit
        Assert.Equal(0, s.Stacks);
        Assert.Equal(22, s.LastHp);
    }

    [Fact]
    public void HealingDoesNotResetTheRamp()
    {
        var s = Seed(turns: 0, hp: 20);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 20, cap: Cap);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 40, cap: Cap);   // HP rose: not a hit
        Assert.Equal(1, s.Stacks);
    }

    [Fact]
    public void AHitWinsOverASimultaneousTurn()
    {
        var s = Seed(turns: 0, hp: 30);
        s = AfterimagePolicy.Step(s, turns: 2, hp: 30, cap: Cap);
        // same window: a new turn AND a damage drop -> the hit wins, ramp resets
        s = AfterimagePolicy.Step(s, turns: 3, hp: 18, cap: Cap);
        Assert.Equal(0, s.Stacks);
    }

    [Fact]
    public void UnreadableHpNeitherResetsNorLosesTheLastReading()
    {
        var s = Seed(turns: 0, hp: 30);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 30, cap: Cap);
        s = AfterimagePolicy.Step(s, turns: 1, hp: 0, cap: Cap);    // 0 == unreadable, not death-by-hit
        Assert.Equal(1, s.Stacks);
        Assert.Equal(30, s.LastHp);                                 // keeps the last good HP
        // and a real hit is still detected on the next good reading
        s = AfterimagePolicy.Step(s, turns: 1, hp: 25, cap: Cap);
        Assert.Equal(0, s.Stacks);
    }

    [Fact]
    public void SpeedBonusScalesWithStacksAndPerTurn()
    {
        var s = Seed(turns: 0, hp: 30);
        s = AfterimagePolicy.Step(s, turns: 3, hp: 30, cap: Cap);
        Assert.Equal(3, AfterimagePolicy.SpeedBonus(s, PerTurn));
        Assert.Equal(6, AfterimagePolicy.SpeedBonus(s, perTurn: 2));
    }

    [Fact]
    public void IsActiveRequiresTheFlagAndTheEarnedTier()
    {
        var sig = new WeaponSignature { AtTier = 3, Afterimage = true };
        Assert.False(AfterimagePolicy.IsActive(sig, tier: 2));   // not yet earned
        Assert.True(AfterimagePolicy.IsActive(sig, tier: 3));
        Assert.False(AfterimagePolicy.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));  // flag off
        Assert.False(AfterimagePolicy.IsActive(null, tier: 3));
    }

    private static AfterimageState Seed(int turns, int hp)
        => AfterimagePolicy.Step(AfterimageState.Empty, turns, hp, Cap);
}
