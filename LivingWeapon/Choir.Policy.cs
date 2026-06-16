using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Warlock's Staff's "Choir" signature -- no memory access.
/// The stateful per-tick aura writer and bearer-alive gate live in Choir.cs.
/// </summary>
internal sealed partial class Choir
{
    /// <summary>True when the signature is configured (InstantCastRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.InstantCastRadius > 0;

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>Chebyshev distance between two grid tiles: max(|dx|, |dy|). Unlike Manhattan
    /// distance, diagonals count as 1, matching the 8-directional "king move" neighbourhood.</summary>
    public static int Chebyshev(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    /// <summary>True when a unit at (x,y) is inside the aura centred on the wielder at (wx,wy):
    /// Chebyshev distance &lt;= radius. The wielder itself is distance 0. Diagonals are
    /// distance 1 (the key distinction from Wyrmblood's Manhattan metric).</summary>
    public static bool InAura(int wx, int wy, int x, int y, int radius)
        => Chebyshev(wx, wy, x, y) <= radius;

    /// <summary>The cap: pick the nearest <paramref name="max"/> UNIQUE unit fingerprints from the
    /// in-aura candidates (each paired with its Chebyshev distance to the bearer). A nearer ENTRY of
    /// an already-won unit (a band twin / fingerprint collision) never consumes a second slot. The
    /// sort is stable, so equal-distance ties resolve by the caller's order (band-slot order) --
    /// deterministic. The bearer is distance 0, so when present it is always chosen first.</summary>
    public static HashSet<(int mhp, int lvl, int br, int fa)> SelectNearest(
        IReadOnlyList<((int mhp, int lvl, int br, int fa) fp, int dist)> candidates, int max)
    {
        var winners = new HashSet<(int mhp, int lvl, int br, int fa)>();
        if (max <= 0) return winners;
        foreach (var c in candidates.OrderBy(c => c.dist))
        {
            if (winners.Contains(c.fp)) continue;   // a nearer entry of this unit already won
            if (winners.Count >= max) break;
            winners.Add(c.fp);
        }
        return winners;
    }
}
