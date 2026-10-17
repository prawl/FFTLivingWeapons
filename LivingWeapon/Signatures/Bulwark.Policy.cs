using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for the Sunderer's "Bulwark" signature, unit-tested in BulwarkPolicyTests.cs.
/// The stateful trigger (turn-flag edges, terrain-grid reads/writes, restore book) is in Bulwark.cs.
/// Provenance (docs/BULWARK_AC.md): terrain grid at 0x140D8DCB0 (Offsets.PathTerrainGrid, base
/// corrected 2026-07-28), 8 bytes/tile, idx = x + y*mapWidth + layerBit*0x100. Byte +6 bit 0x02 is
/// the engine's obstacle state: blocks movement and enemy pathing, tile stays hoverable (LIVE_LEDGER
/// Contradicted-section terrain entry, 2026-07-28). Facing = band +0x35 low 2 bits (0=S 1=W 2=N 3=E),
/// same entry. Map WxH at 0x140C6AD6A (Offsets.MapDimsWH) read 11x12 and 10x18 on the two probe maps.
/// </summary>
internal static class BulwarkPolicy
{
    /// <summary>The tile directly behind the wielder (docs/BULWARK_AC.md): the wielder keeps its
    /// mobility and allies are not walled, but the back-attack tile is denied. facing is already
    /// masked to 0..3 (Offsets.ALayerBit low 2 bits); behind is opposite the look direction:
    ///   facing 0 South (looks 0,-1)  -&gt; behind (x,   y+1), requires y &lt; h-1
    ///   facing 1 West  (looks -1,0)  -&gt; behind (x+1, y),   requires x &lt; w-1
    ///   facing 2 North (looks 0,+1)  -&gt; behind (x,   y-1), requires y &gt; 0
    ///   facing 3 East  (looks +1,0)  -&gt; behind (x-1, y),   requires x &gt; 0
    /// Corrected 2026-07-28 06:15: the Y axis was inverted (a sibling repo's "y+ = south" note is
    /// wrong for this grid). A facing-North plant at (9,8) barred (9,9), in front of the unit; at
    /// battle start enemies at low y facing players at high y read facing byte 2, so North looks
    /// toward +y. The game's labels are kept, only the Y delta flipped.
    /// Each axis is bounds-checked before index math, since x-1/y-1 at the map edge would alias a
    /// tile on another row. Returns null when the tile falls off the map. idx = x + y*w +
    /// layerBit*0x100, as the pathfinder addresses it.</summary>
    internal static (int x, int y, int idx)? BehindTile(int x, int y, int facing, int w, int h, int layerBit)
    {
        int bx = facing switch
        {
            1 => x + 1,   // facing West -> behind is East
            3 => x - 1,   // facing East -> behind is West
            _ => x,
        };
        int by = facing switch
        {
            0 => y + 1,   // facing South (looks 0,-1) -> behind is at y+1
            2 => y - 1,   // facing North (looks 0,+1) -> behind is at y-1
            _ => y,
        };

        if (bx < 0 || bx > w - 1 || by < 0 || by > h - 1) return null;

        int layerOffset = layerBit * 0x100;
        return (bx, by, bx + by * w + layerOffset);
    }

    /// <summary>Grid byte +6 with the obstacle bit (Offsets.PathTerrainVetoBit, 0x02) set and the
    /// other bits kept; idempotent (a tree's 0x22 stays 0x22). Restore writes back the saved
    /// original, not this value.</summary>
    internal static byte VetoedF6(byte orig) => (byte)(orig | Offsets.PathTerrainVetoBit);

    /// <summary>Plant decision at the wielder's turn-end (falling turn-flag edge): true when the
    /// turn had no move and no action. Same shape as MushinPolicy.ShouldArm (see Mushin.cs).</summary>
    internal static bool ShouldPlant(bool turnEnded, bool moved, bool acted)
        => turnEnded && !moved && !acted;

    /// <summary>Release on the rising edge of the wielder's turn flag (0 -&gt; 1, AC criterion 2).</summary>
    internal static bool ShouldRelease(int prevFlag, int curFlag)
        => prevFlag == 0 && curFlag == 1;

    /// <summary>True when <paramref name="tile"/> is not in the occupancy set (AC criterion 5; the
    /// set is loose, see Bulwark.BuildOccupancy).</summary>
    internal static bool IsVacant((int x, int y) tile, HashSet<(int x, int y)> occupied)
        => !occupied.Contains(tile);

    /// <summary>Sanity floor (AC A3) on map WxH and the wielder's position: 1..30 per axis, area at
    /// most 256, wielder inside the map. On failure the caller refuses to plant.</summary>
    internal static bool DimsSane(int w, int h, int gx, int gy)
        => w >= 1 && w <= 30 && h >= 1 && h <= 30 && w * h <= 256 && gx < w && gy < h;
}
