using System;

namespace LivingWeapon;

/// <summary>
/// Shared grid-tile metrics for band-position signatures. Moved out of Ricochet.Policy.cs
/// (LW-149 stage G) after Wyrmblood.Policy.InSplash (deleted in LW-159) borrowed Ricochet's
/// Manhattan helper. Ricochet.Policy keeps a one-line forward.
/// </summary>
internal static class Grid
{
    /// <summary>Manhattan (taxicab) distance between two grid cells.</summary>
    public static int Manhattan(int x1, int y1, int x2, int y2)
        => Math.Abs(x2 - x1) + Math.Abs(y2 - y1);
}
