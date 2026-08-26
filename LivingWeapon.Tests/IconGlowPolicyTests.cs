using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-295 cycle B (needle machinery retired LW-336): IconGlowPolicy's pure half -- no I/O, no
/// store, no manifest. DesiredTiers and Diff drive the tick-facing desired-vs-applied comparison
/// that IconGlow.cs (the diff) and IconGlow.Apply.cs (the judge + apply) act on. Every test here
/// compiles WITHOUT LWDEV (LivingWeapon.Tests.csproj only defines it when dotnet test is given
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
        // An id absent from applied is treated as tier 0 only PROVISIONALLY: the background
        // judge (IconGlow.Apply.cs) corrects _applied from the real deployed tex before any
        // write happens, so a not-yet-seeded id never causes a wrong overwrite -- see that
        // file's class doc comment for why the old "the pac is rebuilt every launch" premise
        // this used to cite no longer holds.
        var applied = new Dictionary<int, int>();
        var desired = new Dictionary<int, int> { [9] = 1 };

        var changed = IconGlowPolicy.Diff(applied, desired);

        Assert.Equal(1, changed[9]);
    }
}
