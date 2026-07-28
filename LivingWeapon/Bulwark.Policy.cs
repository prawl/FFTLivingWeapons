using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for the Sunderer's "Bulwark" signature: no memory access, unit-tested directly
/// (BulwarkPolicyTests.cs). The stateful trigger (per-tick turn-flag edge tracking, the terrain-
/// grid reads/writes, the restore book) lives in Bulwark.cs.
///
/// PROVENANCE (docs/BULWARK_AC.md): the terrain grid at 0x140D8DCB0 (Offsets.PathTerrainGrid,
/// base corrected 2026-07-28), 8 bytes/tile, idx = x + y*mapWidth + layerBit*0x100, byte +6 bit
/// 0x02 = the engine's OWN obstacle state (blocks movement + enemy AI pathing while leaving the
/// tile hoverable, the native red circle-slash cursor) -- owner-proven live 2026-07-28
/// (LIVE_LEDGER Contradicted-section terrain entry). Facing = band +0x35 low 2 bits (0=S 1=W 2=N
/// 3=E), same ledger entry, 2026-07-28. The map WxH pair (0x140C6AD6A, Offsets.MapDimsWH) read
/// 11x12 and 10x18 on the two live probe maps.
/// </summary>
internal static class BulwarkPolicy
{
    /// <summary>The ONE tile directly BEHIND the wielder ("the anti-Provoke, the leave-me-alone
    /// move", docs/BULWARK_AC.md): the wielder keeps its own mobility and allies are never walled,
    /// but the back tile -- and so the back-attack bonus -- is denied. facing arrives already
    /// masked to 0..3 (Offsets.ALayerBit low 2 bits); behind is the tile opposite the unit's LOOK
    /// direction:
    ///   facing 0 South (looks 0,-1)  -&gt; behind (x,   y+1), requires y &lt; h-1
    ///   facing 1 West  (looks -1,0)  -&gt; behind (x+1, y),   requires x &lt; w-1
    ///   facing 2 North (looks 0,+1)  -&gt; behind (x,   y-1), requires y &gt; 0
    ///   facing 3 East  (looks +1,0)  -&gt; behind (x-1, y),   requires x &gt; 0
    /// LIVE-PASS CORRECTED 2026-07-28 06:15: the Y-axis convention above was inverted before this
    /// fix (inherited from a sibling repo's "y+ = south" note, which is WRONG for this grid).
    /// Owner-witnessed: a facing-North plant at (9,8) barred (9,9) and it rendered IN FRONT of the
    /// unit, not behind it; the companion facing-West plant at (10,8) barred correctly (the X-axis
    /// was never wrong). Corroboration: at battle start, enemies at LOW y facing the players at
    /// HIGH y read facing byte 2 (North) -- North looks toward +y, not -y. The game's own facing
    /// LABELS are kept (byte 2 is still what the game calls North); only the Y-axis delta flipped.
    /// PER-AXIS bounds-guarded BEFORE any index math (the same wrap-trap rationale the old ring
    /// design used): an unguarded x-1/y-1 at the map edge silently aliases a tile on another
    /// row/column. Returns null when the behind tile falls off the declared map (a wielder with
    /// its back to the edge plants nothing). idx matches the pathfinder's own addressing:
    /// x + y*w + layerBit*0x100.</summary>
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

    /// <summary>Grid byte +6 with the obstacle bit (Offsets.PathTerrainVetoBit, 0x02) OR'd in,
    /// preserving every other bit -- idempotent on an already-vetoed byte (a tree's 0x22 stays
    /// 0x22). The restore path writes the exact saved original back, never this derived value.</summary>
    internal static byte VetoedF6(byte orig) => (byte)(orig | Offsets.PathTerrainVetoBit);

    /// <summary>The plant decision, evaluated only at the wielder's own turn-end (the falling
    /// TURN FLAG edge; Bulwark.cs never calls this mid-turn): true only when the whole turn passed
    /// with neither a move nor an action -- exactly MushinPolicy.ShouldArm's shape, the same PSX
    /// turn-flag design (Mushin.cs's class doc has the full provenance).</summary>
    internal static bool ShouldPlant(bool turnEnded, bool moved, bool acted)
        => turnEnded && !moved && !acted;

    /// <summary>The release decision: the RISING edge of the wielder's own turn flag (0 -&gt; 1,
    /// the next turn opening -- AC criterion 2). Bulwark.cs only actually releases when a hold is
    /// currently active; this predicate just names the edge.</summary>
    internal static bool ShouldRelease(int prevFlag, int curFlag)
        => prevFlag == 0 && curFlag == 1;

    /// <summary>True when <paramref name="tile"/> is absent from the caller-built occupancy set
    /// (AC criterion 5: vacant tiles only). The occupancy set itself is deliberately LOOSE (any
    /// plausible unit-or-corpse seat, not Band.IsValid -- see Bulwark.BuildOccupancy).</summary>
    internal static bool IsVacant((int x, int y) tile, HashSet<(int x, int y)> occupied)
        => !occupied.Contains(tile);

    /// <summary>The runtime SANITY GATE floor (AC A3) on the map WxH pair and the wielder's own
    /// grid position: 1..30 on each axis, area capped at 256, and the wielder's own tile must lie
    /// inside the declared map. A failure here means "refuse to plant, log once at Debug" -- never
    /// a partial or best-effort plant.</summary>
    internal static bool DimsSane(int w, int h, int gx, int gy)
        => w >= 1 && w <= 30 && h >= 1 && h <= 30 && w * h <= 256 && gx < w && gy < h;
}
