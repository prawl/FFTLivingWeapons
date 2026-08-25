using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-295 cycle B: IconGlowPolicy's pure half -- no I/O, no store, no manifest. DesiredTiers and
/// Diff drive the tick-facing desired-vs-applied comparison (IconGlow.cs); FindNeedle/
/// ClassifyHitCount drive the pac needle search (IconGlow.Apply.cs). Every test here compiles
/// WITHOUT LWDEV (LivingWeapon.Tests.csproj only defines it when dotnet test is given
/// -p:LwDev=true), so Tuning.TierOf resolves the PROD curve {5,10,15} -- every kill count below
/// is chosen against that curve, one below and one at each boundary.
/// </summary>
public class IconGlowPolicyTests
{
    // ---- U1: DesiredTiers uses Tuning's real threshold curve, not a re-derived one ----

    [Fact]
    public void DesiredTiers_UsesTuningThresholds()
    {
        var kills = new Dictionary<int, int>
        {
            [1] = 0, [2] = 5, [3] = 10, [4] = 15,   // tier boundaries: 0/1/2/3
            [5] = 4, [6] = 9, [7] = 14,              // one kill short of each boundary: 0/1/2
        };
        var desired = IconGlowPolicy.DesiredTiers(kills, new[] { 1, 2, 3, 4, 5, 6, 7 });

        Assert.Equal(0, desired[1]);
        Assert.Equal(1, desired[2]);
        Assert.Equal(2, desired[3]);
        Assert.Equal(3, desired[4]);
        Assert.Equal(0, desired[5]);
        Assert.Equal(1, desired[6]);
        Assert.Equal(2, desired[7]);
    }

    [Fact]
    public void DesiredTiers_IdAbsentFromKills_IsTierZero()
    {
        var desired = IconGlowPolicy.DesiredTiers(new Dictionary<int, int>(), new[] { 42 });
        Assert.Equal(0, desired[42]);
    }

    // ---- U2: Diff lists only the ids whose desired tier differs from applied ----

    [Fact]
    public void Diff_OnlyChangedIcons_Listed()
    {
        var applied = new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 2 };
        var desired = new Dictionary<int, int> { [1] = 0, [2] = 2, [3] = 2, [4] = 1 };

        var changed = IconGlowPolicy.Diff(applied, desired);

        Assert.Equal(2, changed.Count);
        Assert.Equal(2, changed[2]);
        Assert.Equal(1, changed[4]);
        Assert.False(changed.ContainsKey(1));
        Assert.False(changed.ContainsKey(3));
    }

    [Fact]
    public void Diff_EmptyWhenAppliedEqualsDesired()
    {
        var applied = new Dictionary<int, int> { [1] = 2 };
        var desired = new Dictionary<int, int> { [1] = 2 };

        Assert.Empty(IconGlowPolicy.Diff(applied, desired));
    }

    [Fact]
    public void Diff_IdMissingFromApplied_CountsAsTierZero()
    {
        // P6: the pac is rebuilt from loose files every launch, so an id this runtime has never
        // touched yet is guaranteed to still be sitting at tier-0 base art.
        var applied = new Dictionary<int, int>();
        var desired = new Dictionary<int, int> { [9] = 1 };

        var changed = IconGlowPolicy.Diff(applied, desired);

        Assert.Equal(1, changed[9]);
    }

    // ---- U3: needle verdict classification -- exactly one hit is the only manageable case ----

    [Fact]
    public void ClassifyHitCount_Zero_IsNotFound()
        => Assert.Equal(IconGlowPolicy.NeedleVerdict.NotFound, IconGlowPolicy.ClassifyHitCount(0));

    [Fact]
    public void ClassifyHitCount_One_IsFoundOnce()
        => Assert.Equal(IconGlowPolicy.NeedleVerdict.FoundOnce, IconGlowPolicy.ClassifyHitCount(1));

    [Fact]
    public void ClassifyHitCount_Two_IsAmbiguous()
        => Assert.Equal(IconGlowPolicy.NeedleVerdict.Ambiguous, IconGlowPolicy.ClassifyHitCount(2));

    [Fact]
    public void FindNeedle_ExactlyOnce_ReturnsHitAndOffset()
    {
        byte[] haystack = { 9, 9, 1, 2, 3, 9, 9, 9 };
        byte[] needle = { 1, 2, 3 };

        int hits = IconGlowPolicy.FindNeedle(haystack, needle, out long offset);

        Assert.Equal(1, hits);
        Assert.Equal(2, offset);
    }

    [Fact]
    public void FindNeedle_NotPresent_ReturnsZeroHits()
    {
        byte[] haystack = { 9, 9, 9, 9 };
        byte[] needle = { 1, 2, 3 };

        int hits = IconGlowPolicy.FindNeedle(haystack, needle, out long offset);

        Assert.Equal(0, hits);
        Assert.Equal(-1, offset);
    }

    [Fact]
    public void FindNeedle_MultipleOccurrences_CapsCountAtTwo()
    {
        // Truly occurs three times; the cap only ever needs to know ">1", never the real count,
        // so a common byte run never forces scanning the rest of a 64MB buffer.
        byte[] haystack = { 1, 2, 3, 9, 1, 2, 3, 9, 1, 2, 3 };
        byte[] needle = { 1, 2, 3 };

        int hits = IconGlowPolicy.FindNeedle(haystack, needle, out long offset);

        Assert.Equal(2, hits);
        Assert.Equal(0, offset);   // the first hit's offset is still correct even once capped
    }
}
