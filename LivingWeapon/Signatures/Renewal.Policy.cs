using System;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Mending Staff's "Renewal" signature -- no memory access.
/// The stateful turn-edge watcher, ally filter, and guarded heals live in Renewal.cs.
/// </summary>
internal sealed partial class Renewal
{
    /// <summary>True when the signature is configured (RegenAuraRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.RegenAuraRadius > 0;

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>The wielder's turn edge -- THE rule lives on the shared core
    /// (<see cref="HealPulse.IsTurnEdge"/>; LW-153: this was one of two verbatim copies).
    /// Kept as a delegating alias so the policy surface and its tests read per-module.</summary>
    public static bool IsTurnEdge(int lastTurns, int turns) => HealPulse.IsTurnEdge(lastTurns, turns);

    /// <summary>Chebyshev distance between two grid tiles: max(|dx|, |dy|). Unlike Manhattan
    /// distance, diagonals count as 1, matching the 8-directional "king move" neighbourhood.</summary>
    public static int Chebyshev(int x1, int y1, int x2, int y2)
        => Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    /// <summary>True when a unit at (x,y) is inside the aura centred on the wielder at (wx,wy):
    /// Chebyshev distance &lt;= radius. The wielder itself is distance 0. Diagonals are
    /// distance 1 (under a Manhattan metric they would be 2 and fall outside radius 1).</summary>
    public static bool InAura(int wx, int wy, int x, int y, int radius)
        => Chebyshev(wx, wy, x, y) <= radius;
}
