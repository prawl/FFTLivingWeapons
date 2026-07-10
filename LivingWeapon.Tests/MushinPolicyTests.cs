using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Mushin policy (MushinPolicy): the one-shot charge computation behind Kiku-ichimonji's
/// stillness buff, plus PaHeld's growth-formula math. No memory access: the stateful trigger
/// (the per-wielder TURN FLAG edge tracking in Mushin.cs) is exercised by MushinTests.cs.
///
/// ROUND 5 (2026-07-09, owner decision: replace rounds 2-4's CT-clock/seq-gate apparatus with the
/// literal design on the engine's own per-unit turn flags; see Mushin.cs's class doc for the full
/// provenance): ShouldArm and ShouldConsume are back to the ORIGINAL round-1 truth-table shapes
/// (full circle). StacksFromDeltas (the CT-cycle aggregation) and ConfirmsPerformance (the
/// action-record pending-confirm truth table) are retired with rounds 2-4.
/// </summary>
public class MushinPolicyTests
{
    // ── ShouldArm (P-1: the arming truth table) ────────────────────────────────────────────────

    [Fact]
    public void ShouldArm_TurnEnded_NoMoveNoAct_True()
        => Assert.True(MushinPolicy.ShouldArm(turnEnded: true, moved: false, acted: false));

    [Fact]
    public void ShouldArm_TurnEnded_Moved_False()
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: true, moved: true, acted: false));

    [Fact]
    public void ShouldArm_TurnEnded_Acted_False()
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: true, moved: false, acted: true));

    [Fact]
    public void ShouldArm_TurnEnded_MovedAndActed_False()
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: true, moved: true, acted: true));

    [Fact]
    public void ShouldArm_TurnNotEnded_NeverArms_RegardlessOfMovedActed()
    {
        Assert.False(MushinPolicy.ShouldArm(turnEnded: false, moved: false, acted: false));
        Assert.False(MushinPolicy.ShouldArm(turnEnded: false, moved: true, acted: false));
        Assert.False(MushinPolicy.ShouldArm(turnEnded: false, moved: false, acted: true));
        Assert.False(MushinPolicy.ShouldArm(turnEnded: false, moved: true, acted: true));
    }

    // ── ShouldConsume (P-2: the consume truth table) ───────────────────────────────────────────

    [Fact]
    public void ShouldConsume_TurnEnded_Acted_True()
        => Assert.True(MushinPolicy.ShouldConsume(turnEnded: true, acted: true));

    [Fact]
    public void ShouldConsume_TurnEnded_NotActed_False()
        => Assert.False(MushinPolicy.ShouldConsume(turnEnded: true, acted: false));

    [Fact]
    public void ShouldConsume_TurnNotEnded_Acted_False()
        => Assert.False(MushinPolicy.ShouldConsume(turnEnded: false, acted: true));

    [Fact]
    public void ShouldConsume_TurnNotEnded_NotActed_False()
        => Assert.False(MushinPolicy.ShouldConsume(turnEnded: false, acted: false));

    [Fact]
    public void ShouldArm_And_ShouldConsume_AreMutuallyExclusive_WhenActed()
    {
        // acted=true fails ShouldArm's !acted term unconditionally, so the two predicates can
        // never both fire for the same falling edge; Mushin.cs's consume-first branching relies
        // on this, not on evaluation order.
        Assert.True(MushinPolicy.ShouldConsume(turnEnded: true, acted: true));
        Assert.False(MushinPolicy.ShouldArm(turnEnded: true, moved: false, acted: true));
        Assert.False(MushinPolicy.ShouldArm(turnEnded: true, moved: true, acted: true));
    }

    // ── PaHeld (P-3: kept VERBATIM; the constants pin updates to the one-shot values) ──────────

    private static readonly double[] LockedFactor = { 0.00, 0.10, 0.20, 0.30 };   // Tuning.Factor, duplicated to catch drift

    [Fact]
    public void PaHeld_NotArmed_MatchesLockedFactorTable()
    {
        Assert.Equal(4, Tuning.Factor.Length);
        for (int t = 0; t < 4; t++)
            Assert.Equal(LockedFactor[t], Tuning.Factor[t]);
    }

    [Fact]
    public void MushinBonus_And_MaxStacks_AreLocked()
    {
        // Pins the tuned values this whole test file's expectations are built against.
        Assert.Equal(0.75, Tuning.MushinBonus);
        Assert.Equal(1, Tuning.MushinMaxStacks);   // one-shot: one wait, one charged hit
    }

    [Fact]
    public void PaHeld_ZeroStacks_Tier0_IsUnchanged()
        => Assert.Equal(12, MushinPolicy.PaHeld(naturalPa: 12, tier: 0, LockedFactor, stacks: 0, bonus: 0.75));

    [Fact]
    public void PaHeld_ZeroStacks_Tier3_ByteIdenticalToNormalGrowth()
    {
        // 12 * (1 + 0.30) = 15.6 -> Math.Round default (banker's / ToEven) = 16.
        int expected = (int)System.Math.Round(12 * (1 + LockedFactor[3]));
        Assert.Equal(expected, MushinPolicy.PaHeld(naturalPa: 12, tier: 3, LockedFactor, stacks: 0, bonus: 0.75));
    }

    [Fact]
    public void PaHeld_OneStack_AddsBonusOnce()
    {
        // tier 3 (0.30) + 1*0.75 = 1.05 -> 12 * 2.05 = 24.6 -> round = 25.
        int expected = (int)System.Math.Round(12 * (1 + LockedFactor[3] + 0.75));
        int actual = MushinPolicy.PaHeld(naturalPa: 12, tier: 3, LockedFactor, stacks: 1, bonus: 0.75);
        Assert.Equal(expected, actual);
        Assert.True(actual > MushinPolicy.PaHeld(12, 3, LockedFactor, stacks: 0, bonus: 0.75));
    }

    [Fact]
    public void PaHeld_TwoStacks_AddsBonusTwice()
    {
        // Not reachable at runtime under the one-shot design (armed is 0/1), but PaHeld is a pure
        // formula independent of that cap; pinning its math for arbitrary stacks catches drift.
        int expected = (int)System.Math.Round(12 * (1 + LockedFactor[3] + 2 * 0.75));
        int actual = MushinPolicy.PaHeld(naturalPa: 12, tier: 3, LockedFactor, stacks: 2, bonus: 0.75);
        Assert.Equal(expected, actual);
        Assert.True(actual > MushinPolicy.PaHeld(12, 3, LockedFactor, stacks: 1, bonus: 0.75));
    }

    [Fact]
    public void PaHeld_ThreeStacks_AddsBonusThrice()
    {
        int expected = (int)System.Math.Round(12 * (1 + LockedFactor[3] + 3 * 0.75));
        int actual = MushinPolicy.PaHeld(naturalPa: 12, tier: 3, LockedFactor, stacks: 3, bonus: 0.75);
        Assert.Equal(expected, actual);
        Assert.True(actual > MushinPolicy.PaHeld(12, 3, LockedFactor, stacks: 2, bonus: 0.75));
    }

    [Fact]
    public void PaHeld_UsesBankersRounding_MatchingWriteTarget()
        // 10 * 1.05 = 10.5 -> ToEven rounds to 10 (even), NOT AwayFromZero's 11: this is the
        // deliberate divergence FROM Ultima (which uses AwayFromZero) and TOWARD GrowthEngine.WriteTarget.
        => Assert.Equal(10, MushinPolicy.PaHeld(naturalPa: 10, tier: 0, new[] { 0.05, 0.0, 0.0, 0.0 }, stacks: 0, bonus: 0));

    [Fact]
    public void PaHeld_ClampsAtUpperBound255()
        => Assert.Equal(255, MushinPolicy.PaHeld(naturalPa: 200, tier: 3, LockedFactor, stacks: 3, bonus: 5.0));

    [Fact]
    public void PaHeld_ClampsAtLowerBound1()
        // Contrived negative bonus exercises the floor clamp directly (real Tuning.MushinBonus is
        // never negative; this pins the defensive clamp itself, not a reachable game state).
        => Assert.Equal(1, MushinPolicy.PaHeld(naturalPa: 5, tier: 0, new[] { 0.0, 0.0, 0.0, 0.0 }, stacks: 1, bonus: -2.0));

    // ── EffectiveStacks (kept VERBATIM: the below-AtTier gate) ────────────────

    [Fact]
    public void EffectiveStacks_AtOrAboveTier_ReturnsStacks()
        => Assert.Equal(1, MushinPolicy.EffectiveStacks(stacks: 1, tier: 3, atTier: 3));

    [Fact]
    public void EffectiveStacks_BelowAtTier_ReturnsZero()
        // Even if the trigger's own bookkeeping somehow carried a charge below tier, the bonus never lands.
        => Assert.Equal(0, MushinPolicy.EffectiveStacks(stacks: 1, tier: 2, atTier: 3));

    [Fact]
    public void EffectiveStacks_ZeroStacks_ReturnsZero_RegardlessOfTier()
        => Assert.Equal(0, MushinPolicy.EffectiveStacks(stacks: 0, tier: 3, atTier: 3));

    // ── OwnsMushin (GrowthEngine's lane-ownership helper) ─────────────────────

    [Fact]
    public void OwnsMushin_TrueOnlyWhenMushinFlagSet()
    {
        var mYes  = new WeaponMeta { Signature = new WeaponSignature { Mushin = true } };
        var mNo   = new WeaponMeta { Signature = new WeaponSignature { Mushin = false } };
        var mNull = new WeaponMeta { Signature = null };
        Assert.True(GrowthEngine.OwnsMushin(mYes));
        Assert.False(GrowthEngine.OwnsMushin(mNo));
        Assert.False(GrowthEngine.OwnsMushin(mNull));
    }
}
