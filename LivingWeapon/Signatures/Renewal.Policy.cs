using System;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for Mending Staff's Renewal signature, no memory access.
/// The turn-edge watcher, ally filter and heals live in Renewal.cs.
/// </summary>
internal sealed partial class Renewal
{
    /// <summary>True when RegenAuraRadius is set and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.RegenAuraRadius > 0;

    /// <summary>The weapon must be in RRHand to activate. Kills count from any hand, but the gift fires only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>Delegates to <see cref="HealPulse.IsTurnEdge"/> (LW-153 dedup) so the policy surface reads per-module.</summary>
    public static bool IsTurnEdge(int lastTurns, int turns) => HealPulse.IsTurnEdge(lastTurns, turns);

    /// <summary>max(|dx|, |dy|), so diagonals count as 1.</summary>
    public static int Chebyshev(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    /// <summary>True when the tile is within <c>radius</c> of the wielder by Chebyshev distance.</summary>
    public static bool InAura(int wx, int wy, int x, int y, int radius)
        => Chebyshev(wx, wy, x, y) <= radius;
}
