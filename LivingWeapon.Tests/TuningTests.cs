using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The growth knobs that detection/growth/display all share. TierFor's kills->tier mapping
/// must hold at every threshold boundary (regardless of whether the build compiled the dev
/// {1,2,3} or production {5,20,50} thresholds), and the DEV kill-seed must floor every weapon
/// to P3 for fast in-game testing without ever clobbering an already-higher count.
/// </summary>
public class TuningTests
{
    [Fact]
    public void TierFor_maps_kills_to_tier_at_each_boundary()
    {
        var t = Tuning.KillThresholds;
        Assert.Equal(0, Tuning.TierFor(0));
        Assert.Equal(0, Tuning.TierFor(t[0] - 1));
        Assert.Equal(1, Tuning.TierFor(t[0]));
        Assert.Equal(1, Tuning.TierFor(t[1] - 1));
        Assert.Equal(2, Tuning.TierFor(t[1]));
        Assert.Equal(2, Tuning.TierFor(t[2] - 1));
        Assert.Equal(3, Tuning.TierFor(t[2]));
        Assert.Equal(3, Tuning.TierFor(t[2] + 1000));
    }

    [Fact]
    public void Dev_seed_lands_at_P3_under_the_dev_thresholds()
    {
        // The dev seed lands AT P3 so every +3 signature is live the moment a weapon is equipped
        // (the rod pass put all five signatures at +3; a P2 seed forced a grind kill per weapon
        // before anything could be tested). Checked against DevThresholds directly, so it's valid
        // even though tests compile under prod.
        Assert.Equal(3, Tuning.TierForIn(Tuning.DevKillSeed, Tuning.DevThresholds));    // seeded -> P3
    }

    [Fact]
    public void SeedKills_floors_every_weapon_to_the_seed()
    {
        var kills = new Dictionary<int, int>();
        Tuning.SeedKills(new[] { 10, 20, 30 }, kills, 3);
        Assert.Equal(3, kills[10]);
        Assert.Equal(3, kills[20]);
        Assert.Equal(3, kills[30]);
    }

    [Fact]
    public void SeedKills_is_nondestructive_above_the_floor()
    {
        var kills = new Dictionary<int, int> { { 10, 50 }, { 20, 0 } };
        Tuning.SeedKills(new[] { 10, 20, 30 }, kills, 3);
        Assert.Equal(50, kills[10]);   // already higher -> left alone
        Assert.Equal(3, kills[20]);    // below the floor -> raised
        Assert.Equal(3, kills[30]);    // absent -> added at the floor
    }

    // ---- Reliquary Phase 1: Mark thresholds + archetype headroom ----

    [Fact]
    public void Dev_and_prod_mark_thresholds_are_both_always_compiled()
    {
        // Tests compile under prod (no LWDEV) and must still be able to reason about the dev
        // curve directly -- mirrors DevThresholds/ProdThresholds above (Tuning.cs:15-18 idiom).
        Assert.Equal(2, Tuning.DevMarkThresholds[0]);
        Assert.Equal(25, Tuning.ProdMarkThresholds[0]);
    }

    [Fact]
    public void MaxArchetypes_leaves_headroom_for_every_non_unknown_archetype()
    {
        int nonUnknown = 0;
        foreach (VictimClass.Archetype a in System.Enum.GetValues(typeof(VictimClass.Archetype)))
            if (a != VictimClass.Archetype.Unknown) nonUnknown++;
        Assert.True(nonUnknown <= Tuning.MaxArchetypes,
            $"{nonUnknown} non-Unknown archetypes exceeds MaxArchetypes ({Tuning.MaxArchetypes})");
    }
}
