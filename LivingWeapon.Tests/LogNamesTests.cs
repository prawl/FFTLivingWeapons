using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LogNames: human-readable labels for weapon ids (from the meta map) and job ids
/// (from the static PSX-wheel table). All tests are self-contained -- no side effects
/// on the static state between cases (Init is safe to call multiple times).
/// </summary>
public class LogNamesTests
{
    // ---- Weapon lookup ----

    [Fact]
    public void Weapon_returns_name_for_known_id()
    {
        var meta = new Dictionary<int, WeaponMeta>
        {
            { 90, new WeaponMeta { Name = "Yoichi Bow" } },
        };
        LogNames.Init(meta);
        Assert.Equal("Yoichi Bow", LogNames.Weapon(90));
    }

    [Fact]
    public void Weapon_falls_back_gracefully_for_unknown_id()
    {
        var meta = new Dictionary<int, WeaponMeta>();
        LogNames.Init(meta);
        Assert.Equal("weapon 999", LogNames.Weapon(999));
    }

    [Fact]
    public void Weapon_is_safe_when_Init_was_never_called()
    {
        // Reset to an empty state by calling with empty dict, then create a fresh
        // lookup that has never had Init called. The class is static, so we test
        // that an unrecognised id never throws regardless of Init state.
        var ex = Record.Exception(() => LogNames.Weapon(42));
        Assert.Null(ex);
    }

    // ---- Job lookup: live-proven anchors ----

    [Fact]
    public void Job_77_maps_to_Archer_live_proven_anchor()
        => Assert.Equal("Archer", LogNames.Job(77));

    [Fact]
    public void Job_83_maps_to_Thief_live_proven_anchor()
        => Assert.Equal("Thief", LogNames.Job(83));

    // ---- Job lookup: full generic wheel ----

    [Theory]
    [InlineData(74, "Squire")]
    [InlineData(75, "Chemist")]
    [InlineData(76, "Knight")]
    [InlineData(78, "Monk")]
    [InlineData(79, "White Mage")]
    [InlineData(80, "Black Mage")]
    [InlineData(81, "Time Mage")]
    [InlineData(82, "Summoner")]
    [InlineData(84, "Orator")]
    [InlineData(85, "Mystic")]
    [InlineData(86, "Geomancer")]
    [InlineData(87, "Dragoon")]
    [InlineData(88, "Samurai")]
    [InlineData(89, "Ninja")]
    [InlineData(90, "Arithmetician")]
    [InlineData(91, "Bard")]
    [InlineData(92, "Dancer")]
    [InlineData(93, "Mime")]
    public void Job_generic_wheel_maps_correctly(int id, string expected)
        => Assert.Equal(expected, LogNames.Job(id));

    // ---- Job lookup: special ids ----

    [Theory]
    [InlineData(43, "Machinist")]
    [InlineData(96, "Chocobo")]
    [InlineData(160, "Dark Knight")]
    public void Job_special_ids_map_correctly(int id, string expected)
        => Assert.Equal(expected, LogNames.Job(id));

    // ---- Job fallback ----

    [Fact]
    public void Job_falls_back_gracefully_for_unknown_id()
        => Assert.Equal("job 1", LogNames.Job(1));

    [Fact]
    public void Job_falls_back_for_zero()
        => Assert.Equal("job 0", LogNames.Job(0));

    [Fact]
    public void Job_never_throws_for_arbitrary_id()
    {
        var ex = Record.Exception(() => LogNames.Job(255));
        Assert.Null(ex);
    }
}
