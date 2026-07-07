using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Mushin policy (MushinPolicy): the stack computation behind Kiku-ichimonji's
/// stacking power boost, plus PaHeld's growth-formula math. No memory access: the stateful
/// turn-window tracking (ActorPtr/nameId arrival/departure, move + acted-edge sampling) lives
/// in Mushin.cs and is exercised by MushinTests.cs. Mirrors the coverage bar UltimaTests.cs set
/// for GrowthEngine.Ultima.cs: policy pure tests here, the memory hold exercised by the
/// live-verify step before commit.
///
/// STACKING (reworked from the one-shot design): a full wait turn banks ONE stack, up to
/// MushinPolicy's caller-supplied max (Tuning.MushinMaxStacks == 3); a single attack spends
/// every banked stack in one boosted hit, then resets to 0.
/// </summary>
public class MushinPolicyTests
{
    // ── ShouldArm (the full-wait-turn predicate; unchanged shape from the one-shot design) ──

    [Fact]
    public void ShouldArm_TurnEnded_NoMove_NoAct_Arms()
        => Assert.True(MushinPolicy.ShouldArm(turnEnded: true, movedDuringTurn: false, actedDuringTurn: false));

    [Fact]
    public void ShouldArm_MoveOnly_DoesNotArm()
        // THE NON-VACUOUS NEGATIVE: a naive `ShouldArm(turnEnded) => turnEnded` (ignoring the
        // move/act signals) would wrongly return true here.
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: true, movedDuringTurn: true, actedDuringTurn: false));

    [Fact]
    public void ShouldArm_AttackTurn_DoesNotArm()
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: true, movedDuringTurn: false, actedDuringTurn: true));

    [Fact]
    public void ShouldArm_MoveAndAttack_DoesNotArm()
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: true, movedDuringTurn: true, actedDuringTurn: true));

    [Fact]
    public void ShouldArm_TurnNotEnded_NeverArms_EvenWithNoMoveNoAct()
        // Defensive: the decision only ever fires AT turn end, mid-turn is never enough.
        => Assert.False(MushinPolicy.ShouldArm(turnEnded: false, movedDuringTurn: false, actedDuringTurn: false));

    // ── NextStacks (the stack computation: STACKING, up to 3) ─────────────────

    [Fact]
    public void NextStacks_FromZero_Arming_GoesToOne()
        => Assert.Equal(1, MushinPolicy.NextStacks(currentStacks: 0, shouldArm: true, maxStacks: 3));

    [Fact]
    public void NextStacks_IncrementsSequentially_ZeroToThree()
    {
        int stacks = 0;
        stacks = MushinPolicy.NextStacks(stacks, shouldArm: true, maxStacks: 3);
        Assert.Equal(1, stacks);
        stacks = MushinPolicy.NextStacks(stacks, shouldArm: true, maxStacks: 3);
        Assert.Equal(2, stacks);
        stacks = MushinPolicy.NextStacks(stacks, shouldArm: true, maxStacks: 3);
        Assert.Equal(3, stacks);
    }

    [Fact]
    public void NextStacks_CapsAtMax_DoesNotExceedThree()
        // THE NON-VACUOUS CAP CHECK: a naive `currentStacks + 1` (no Math.Min) overflows past 3.
        => Assert.Equal(3, MushinPolicy.NextStacks(currentStacks: 3, shouldArm: true, maxStacks: 3));

    [Fact]
    public void NextStacks_NotArming_StaysUnchanged()
        => Assert.Equal(2, MushinPolicy.NextStacks(currentStacks: 2, shouldArm: false, maxStacks: 3));

    [Fact]
    public void NextStacks_MoveOnlyTurn_DoesNotIncrement()
        // THE LOAD-BEARING NEGATIVE: composing ShouldArm's move-only result into NextStacks must
        // leave already-banked stacks untouched, not silently add one anyway.
        => Assert.Equal(1, MushinPolicy.NextStacks(currentStacks: 1,
            shouldArm: MushinPolicy.ShouldArm(turnEnded: true, movedDuringTurn: true, actedDuringTurn: false),
            maxStacks: 3));

    [Fact]
    public void NextStacks_AttackTurn_DoesNotIncrement()
        // THE OTHER LOAD-BEARING NEGATIVE: an attacking turn must never bank a stack either.
        => Assert.Equal(1, MushinPolicy.NextStacks(currentStacks: 1,
            shouldArm: MushinPolicy.ShouldArm(turnEnded: true, movedDuringTurn: false, actedDuringTurn: true),
            maxStacks: 3));

    // ── ShouldConsume (keyed on stack count instead of a bool) ────────────────

    [Fact]
    public void ShouldConsume_OneStack_OwnTurnActedEdge_Consumes()
        => Assert.True(MushinPolicy.ShouldConsume(stacks: 1, ownTurnActedEdge: true));

    [Fact]
    public void ShouldConsume_MaxStacks_OwnTurnActedEdge_Consumes()
        => Assert.True(MushinPolicy.ShouldConsume(stacks: 3, ownTurnActedEdge: true));

    [Fact]
    public void ShouldConsume_ZeroStacks_OwnTurnActedEdge_NoConsume()
        => Assert.False(MushinPolicy.ShouldConsume(stacks: 0, ownTurnActedEdge: true));

    [Fact]
    public void ShouldConsume_Stacks_ActedEdgeOutsideWindow_BuffSurvives()
        // Negative consume: an acted edge that did NOT occur inside the wielder's own turn window
        // (an enemy's turn, or the wielder merely reacting/countering) must never consume.
        => Assert.False(MushinPolicy.ShouldConsume(stacks: 2, ownTurnActedEdge: false));

    [Fact]
    public void ShouldConsume_ZeroStacks_NoActedEdge_NoConsume()
        => Assert.False(MushinPolicy.ShouldConsume(stacks: 0, ownTurnActedEdge: false));

    // ── EffectiveStacks (the below-AtTier gate) ───────────────────────────────

    [Fact]
    public void EffectiveStacks_AtOrAboveTier_ReturnsStacks()
        => Assert.Equal(3, MushinPolicy.EffectiveStacks(stacks: 3, tier: 3, atTier: 3));

    [Fact]
    public void EffectiveStacks_BelowAtTier_ReturnsZero()
        // Even if the trigger's own bookkeeping somehow banked stacks below tier, the bonus never lands.
        => Assert.Equal(0, MushinPolicy.EffectiveStacks(stacks: 3, tier: 2, atTier: 3));

    [Fact]
    public void EffectiveStacks_ZeroStacks_ReturnsZero_RegardlessOfTier()
        => Assert.Equal(0, MushinPolicy.EffectiveStacks(stacks: 0, tier: 3, atTier: 3));

    // ── Stacking integration (pure): three waits cap at 3, one attack spends all ──

    [Fact]
    public void Stacking_ThreeWaits_CapAtThree_OneAttackSpendsAll()
    {
        int stacks = 0;
        for (int i = 0; i < 3; i++)
            stacks = MushinPolicy.NextStacks(stacks, MushinPolicy.ShouldArm(true, false, false), maxStacks: 3);
        Assert.Equal(3, stacks);

        // A fourth wait must not overflow past the cap.
        stacks = MushinPolicy.NextStacks(stacks, MushinPolicy.ShouldArm(true, false, false), maxStacks: 3);
        Assert.Equal(3, stacks);

        // ONE attack spends every banked stack in a single hit, not one-at-a-time.
        if (MushinPolicy.ShouldConsume(stacks, ownTurnActedEdge: true)) stacks = 0;
        Assert.Equal(0, stacks);
    }

    // ── PaHeld ─────────────────────────────────────────────────────────────────

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
        Assert.Equal(3, Tuning.MushinMaxStacks);
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
        int expected = (int)System.Math.Round(12 * (1 + LockedFactor[3] + 2 * 0.75));
        int actual = MushinPolicy.PaHeld(naturalPa: 12, tier: 3, LockedFactor, stacks: 2, bonus: 0.75);
        Assert.Equal(expected, actual);
        Assert.True(actual > MushinPolicy.PaHeld(12, 3, LockedFactor, stacks: 1, bonus: 0.75));
    }

    [Fact]
    public void PaHeld_ThreeStacks_AddsBonusThriceAtCap()
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
