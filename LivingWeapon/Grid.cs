using System;

namespace LivingWeapon;

/// <summary>
/// Shared grid-tile metrics for signatures that reason about band positions. Promoted out of
/// Ricochet.Policy.cs (LW-149 stage G): Wyrmblood.Policy.InSplash already borrowed Ricochet's
/// Manhattan helper by name for its own splash-radius math, a cross-signature dependency a
/// neutral home removes. Ricochet.Policy keeps a one-line forward so its own tests and any
/// missed caller keep working unchanged.
/// </summary>
internal static class Grid
{
    /// <summary>Manhattan (taxicab) distance between two grid cells.</summary>
    public static int Manhattan(int x1, int y1, int x2, int y2)
        => Math.Abs(x2 - x1) + Math.Abs(y2 - y1);
}
