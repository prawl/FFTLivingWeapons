using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Pure-decision coverage for the Sunderer's "Bulwark" signature (docs/BULWARK_AC.md). No memory
/// access anywhere in this suite -- every method under test is a static pure function on
/// BulwarkPolicy. Module-level (stateful, IGameMemory-driven) coverage lives in BulwarkTests.cs.
/// </summary>
public class BulwarkPolicyTests
{
    // ---- BehindTile: the facing -> behind-tile mapping (AC criterion 7) ----

    /// <summary>LIVE-PASS CORRECTED 2026-07-28 06:15: South looks toward -y, so its behind tile is
    /// at y+1 (owner-witnessed: the companion facing-North plant barred the tile IN FRONT before
    /// this fix -- see BehindTile's doc for the full provenance).</summary>
    [Fact]
    public void BehindTile_FacingSouth_BehindIsNorth()
    {
        var result = BulwarkPolicy.BehindTile(5, 5, facing: 0, w: 11, h: 12, layerBit: 0);
        Assert.Equal((5, 6, 5 + 6 * 11), result);
    }

    [Fact]
    public void BehindTile_FacingWest_BehindIsEast()
    {
        var result = BulwarkPolicy.BehindTile(5, 5, facing: 1, w: 11, h: 12, layerBit: 0);
        Assert.Equal((6, 5, 6 + 5 * 11), result);
    }

    /// <summary>LIVE-PASS CORRECTED 2026-07-28 06:15: North looks toward +y, so its behind tile is
    /// at y-1 -- this is the exact case the owner caught live (a facing-North plant barred y+1 and
    /// it rendered in front of the unit, not behind).</summary>
    [Fact]
    public void BehindTile_FacingNorth_BehindIsSouth()
    {
        var result = BulwarkPolicy.BehindTile(5, 5, facing: 2, w: 11, h: 12, layerBit: 0);
        Assert.Equal((5, 4, 5 + 4 * 11), result);
    }

    [Fact]
    public void BehindTile_FacingEast_BehindIsWest()
    {
        var result = BulwarkPolicy.BehindTile(5, 5, facing: 3, w: 11, h: 12, layerBit: 0);
        Assert.Equal((4, 5, 4 + 5 * 11), result);
    }

    /// <summary>THE LOAD-BEARING NEGATIVE (top edge). A wielder at (5,0) facing North (looks
    /// toward +y, so its behind tile is y-1 -- LIVE-PASS CORRECTED 2026-07-28 06:15, see
    /// BehindTile's doc): without a per-axis guard, y-1 (-1) folded into the linear index formula
    /// silently wraps to a tile on another row. BehindTile must refuse instead.</summary>
    [Fact]
    public void BehindTile_BehindOffTopEdge_Null()
        => Assert.Null(BulwarkPolicy.BehindTile(5, 0, facing: 2, w: 11, h: 12, layerBit: 0));

    /// <summary>THE LOAD-BEARING NEGATIVE (left edge). A wielder at (0,5) facing East: behind
    /// x=-1. Without a per-axis guard this would fold into idx = -1 + 5*11 = 54, a tile on the row
    /// ABOVE -- the same wrap trap the old Neighbors design closed. BehindTile must refuse.</summary>
    [Fact]
    public void BehindTile_BehindOffLeftEdge_Null_NotAWrappedIndex()
    {
        var result = BulwarkPolicy.BehindTile(0, 5, facing: 3, w: 11, h: 12, layerBit: 0);
        Assert.Null(result);
    }

    [Fact]
    public void BehindTile_BehindOffRightEdge_Null()
        => Assert.Null(BulwarkPolicy.BehindTile(10, 5, facing: 1, w: 11, h: 12, layerBit: 0));

    /// <summary>A wielder at (5,11) [h-1] facing South (looks toward -y, so its behind tile is
    /// y+1 -- LIVE-PASS CORRECTED 2026-07-28 06:15, see BehindTile's doc): behind falls off the
    /// bottom edge (y+1 = h = 12). BehindTile must refuse.</summary>
    [Fact]
    public void BehindTile_BehindOffBottomEdge_Null()
        => Assert.Null(BulwarkPolicy.BehindTile(5, 11, facing: 0, w: 11, h: 12, layerBit: 0));

    [Fact]
    public void BehindTile_LayerBit_Adds0x100()
    {
        var result = BulwarkPolicy.BehindTile(5, 5, facing: 2, w: 11, h: 12, layerBit: 1);
        Assert.Equal((5, 4, 5 + 4 * 11 + 0x100), result);
    }

    // ---- VetoedF6 ----

    [Fact]
    public void VetoedF6_SetsOnlyBit02()
    {
        Assert.Equal((byte)0x02, BulwarkPolicy.VetoedF6(0x00));
        Assert.Equal((byte)0x22, BulwarkPolicy.VetoedF6(0x20));
        Assert.Equal((byte)0xFF, BulwarkPolicy.VetoedF6(0xFD));
    }

    [Fact]
    public void VetoedF6_Idempotent_OnTreeValue()
        => Assert.Equal((byte)0x22, BulwarkPolicy.VetoedF6(0x22));

    // ---- ShouldPlant / ShouldRelease (mirrors MushinPolicy's shape) ----

    [Fact]
    public void ShouldPlant_FullWait_True() => Assert.True(BulwarkPolicy.ShouldPlant(turnEnded: true, moved: false, acted: false));

    [Fact]
    public void ShouldPlant_Moved_False() => Assert.False(BulwarkPolicy.ShouldPlant(turnEnded: true, moved: true, acted: false));

    [Fact]
    public void ShouldPlant_Acted_False() => Assert.False(BulwarkPolicy.ShouldPlant(turnEnded: true, moved: false, acted: true));

    [Fact]
    public void ShouldPlant_NotTurnEnded_False() => Assert.False(BulwarkPolicy.ShouldPlant(turnEnded: false, moved: false, acted: false));

    [Fact]
    public void ShouldRelease_RisingEdge_True() => Assert.True(BulwarkPolicy.ShouldRelease(prevFlag: 0, curFlag: 1));

    [Fact]
    public void ShouldRelease_FallingEdge_False() => Assert.False(BulwarkPolicy.ShouldRelease(prevFlag: 1, curFlag: 0));

    [Fact]
    public void ShouldRelease_NoChange_False()
    {
        Assert.False(BulwarkPolicy.ShouldRelease(0, 0));
        Assert.False(BulwarkPolicy.ShouldRelease(1, 1));
    }

    // ---- IsVacant ----

    [Fact]
    public void IsVacant_EmptySet_True()
        => Assert.True(BulwarkPolicy.IsVacant((3, 4), new HashSet<(int, int)>()));

    [Fact]
    public void IsVacant_OccupiedTile_False()
    {
        var occupied = new HashSet<(int, int)> { (3, 4) };
        Assert.False(BulwarkPolicy.IsVacant((3, 4), occupied));
    }

    // ---- DimsSane (AC A3) ----

    [Fact]
    public void DimsSane_NormalMap_True() => Assert.True(BulwarkPolicy.DimsSane(11, 12, 5, 5));

    [Fact]
    public void DimsSane_ZeroWidth_False() => Assert.False(BulwarkPolicy.DimsSane(0, 12, 0, 0));

    [Fact]
    public void DimsSane_Over30_False() => Assert.False(BulwarkPolicy.DimsSane(31, 12, 5, 5));

    [Fact]
    public void DimsSane_AreaOver256_False() => Assert.False(BulwarkPolicy.DimsSane(20, 20, 5, 5));   // 400 > 256

    [Fact]
    public void DimsSane_WielderOutsideDeclaredMap_False() => Assert.False(BulwarkPolicy.DimsSane(11, 12, 11, 5));   // gx == w
}
