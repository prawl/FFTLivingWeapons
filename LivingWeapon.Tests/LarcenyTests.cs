using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Larceny decisions: active-gating, enemy-only latching, highest-priority buff selection
/// against the band status bytes, expiry counting, and the per-wielder steal ledger. The buff
/// transfer itself is exercised through the proven Reraise/Invisible bits so extending coverage to
/// the marquee buffs is purely adding table rows once they're mapped live.
/// </summary>
public class LarcenyTests
{
    [Fact]
    public void IsActiveRequiresTheFlagAndTheEarnedTier()
    {
        var sig = new WeaponSignature { AtTier = 3, LarcenyTurns = 3 };
        Assert.False(LarcenyPolicy.IsActive(sig, tier: 2));
        Assert.True(LarcenyPolicy.IsActive(sig, tier: 3));
        Assert.False(LarcenyPolicy.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));  // turns 0
        Assert.False(LarcenyPolicy.IsActive(null, tier: 3));
    }

    [Fact]
    public void OnlyEnemiesAreLatched()
    {
        Assert.True(LarcenyPolicy.ShouldLatch(isEnemy: true));
        Assert.False(LarcenyPolicy.ShouldLatch(isEnemy: false));
    }

    [Fact]
    public void PickReturnsNullWhenTheTargetHasNoStealableBuff()
    {
        Assert.Null(LarcenyPolicy.Pick(_ => 0x00));
    }

    [Fact]
    public void PickFindsAStealableBuffByItsBit()
    {
        // Only the +0x47 Invisible bit set -> Invisible is picked.
        var buff = LarcenyPolicy.Pick(off => off == Offsets.AInvisible ? Offsets.AInvisibleBit : (byte)0);
        Assert.NotNull(buff);
        Assert.Equal("Invisible", buff!.Value.Name);
        Assert.Equal(Offsets.AInvisible, buff.Value.Off);
        Assert.Equal(Offsets.AInvisibleBit, buff.Value.Mask);
    }

    [Fact]
    public void PickHonoursPriorityOrderWhenSeveralAreSet()
    {
        // Reraise (0x20) and Invisible (0x10) share +0x47; Reraise is listed first -> wins.
        var buff = LarcenyPolicy.Pick(off =>
            off == Offsets.AReraise ? (byte)(Offsets.AReraiseBit | Offsets.AInvisibleBit) : (byte)0);
        Assert.Equal("Reraise", buff!.Value.Name);
    }

    [Theory]
    [InlineData(0, 0, 3, false)]   // just stolen
    [InlineData(2, 0, 3, false)]   // two wielder turns later, not yet
    [InlineData(3, 0, 3, true)]    // third turn -> fades
    [InlineData(7, 4, 3, true)]    // baseline 4, now 7 -> 3 turns elapsed
    public void ExpiryCountsTheWieldersOwnTurns(int now, int baseline, int turns, bool expired)
    {
        Assert.Equal(expired, LarcenyPolicy.IsExpired(now, baseline, turns));
    }

    [Fact]
    public void StealLedgerHoldsBaselineAndNeverResetsAnActiveHold()
    {
        var st = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        Assert.False(st.IsHeld(reraise));

        st.Steal(reraise, wielderTurns: 5);
        Assert.True(st.IsHeld(reraise));
        Assert.Equal(5, st.BaselineTurns(reraise));

        st.Steal(reraise, wielderTurns: 9);          // re-steal while held: baseline must NOT move
        Assert.Equal(5, st.BaselineTurns(reraise));
    }

    [Fact]
    public void StealLedgerTracksSeveralBuffsAndReleasesThemIndependently()
    {
        var st = new LarcenyState();
        var reraise = (Offsets.AReraise, Offsets.AReraiseBit);
        var invis = (Offsets.AInvisible, Offsets.AInvisibleBit);
        st.Steal(reraise, 1);
        st.Steal(invis, 2);
        Assert.Equal(2, st.Held.Count);

        st.Release(reraise);
        Assert.False(st.IsHeld(reraise));
        Assert.True(st.IsHeld(invis));

        st.Clear();
        Assert.Empty(st.Held);
    }
}
