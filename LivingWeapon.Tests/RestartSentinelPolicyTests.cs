using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-233: pure decision-table tests for RestartSentinelPolicy.ShouldOpenLatch -- the retry-rewind
/// detector's opening condition, no ticking/state involved (RestartSentinelTests.cs covers the
/// stateful half: Tick()'s debounce/grace/re-arm bookkeeping and PresentRevive's latch machinery).
/// </summary>
public class RestartSentinelPolicyTests
{
    private const int Grace = RestartSentinelPolicy.GraceTicks;
    private const int Window = RestartSentinelPolicy.JoinWindowTicks;

    [Fact]
    public void Opens_on_a_qualified_null_credited_heal_from_zero_revive_within_the_window_past_grace()
    {
        Assert.True(RestartSentinelPolicy.ShouldOpenLatch(
            wasCredited: true, healedFromZero: true, haveQualifiedNull: true,
            ticksSinceQualifiedNull: Window, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_opens_without_a_qualified_null()
    {
        Assert.False(RestartSentinelPolicy.ShouldOpenLatch(
            wasCredited: true, healedFromZero: true, haveQualifiedNull: false,
            ticksSinceQualifiedNull: 0, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_opens_on_revives_alone_without_credit()
    {
        Assert.False(RestartSentinelPolicy.ShouldOpenLatch(
            wasCredited: false, healedFromZero: true, haveQualifiedNull: true,
            ticksSinceQualifiedNull: 1, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_opens_on_a_revive_that_did_not_heal_from_zero()
    {
        // The status-death residual: dead-bit-only victims never satisfy healedFromZero.
        Assert.False(RestartSentinelPolicy.ShouldOpenLatch(
            wasCredited: true, healedFromZero: false, haveQualifiedNull: true,
            ticksSinceQualifiedNull: 1, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_opens_when_the_null_is_outside_the_join_window()
    {
        Assert.False(RestartSentinelPolicy.ShouldOpenLatch(
            wasCredited: true, healedFromZero: true, haveQualifiedNull: true,
            ticksSinceQualifiedNull: Window + 1, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_opens_before_the_battle_age_grace_has_elapsed()
    {
        Assert.False(RestartSentinelPolicy.ShouldOpenLatch(
            wasCredited: true, healedFromZero: true, haveQualifiedNull: true,
            ticksSinceQualifiedNull: 1, battleAgeTicks: Grace));
    }

    [Fact]
    public void NullQualifies_requires_the_persist_threshold()
    {
        Assert.False(RestartSentinelPolicy.NullQualifies(RestartSentinelPolicy.NullPersistTicks - 1));
        Assert.True(RestartSentinelPolicy.NullQualifies(RestartSentinelPolicy.NullPersistTicks));
    }

    // ---- ShouldStash (LW-233 deferred-verdict fix: the 0ms-join shape, verifier-caught) ----

    [Fact]
    public void Stashes_a_credited_heal_from_zero_revive_while_a_null_is_mid_flight_past_grace()
    {
        Assert.True(RestartSentinelPolicy.ShouldStash(
            wasCredited: true, healedFromZero: true, nullStreakTicks: 1, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_stashes_without_any_null_in_progress()
    {
        Assert.False(RestartSentinelPolicy.ShouldStash(
            wasCredited: true, healedFromZero: true, nullStreakTicks: 0, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_stashes_a_null_that_has_already_qualified()
    {
        // An already-qualified null takes the DIRECT ShouldOpenLatch path -- ShouldStash is only
        // for the in-progress, not-yet-qualified window.
        Assert.False(RestartSentinelPolicy.ShouldStash(
            wasCredited: true, healedFromZero: true,
            nullStreakTicks: RestartSentinelPolicy.NullPersistTicks, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_stashes_without_credit_or_without_healing_from_zero()
    {
        Assert.False(RestartSentinelPolicy.ShouldStash(
            wasCredited: false, healedFromZero: true, nullStreakTicks: 1, battleAgeTicks: Grace + 1));
        Assert.False(RestartSentinelPolicy.ShouldStash(
            wasCredited: true, healedFromZero: false, nullStreakTicks: 1, battleAgeTicks: Grace + 1));
    }

    [Fact]
    public void Never_stashes_before_the_battle_age_grace_has_elapsed()
    {
        Assert.False(RestartSentinelPolicy.ShouldStash(
            wasCredited: true, healedFromZero: true, nullStreakTicks: 1, battleAgeTicks: Grace));
    }
}
