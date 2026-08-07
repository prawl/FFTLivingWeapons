namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Dragon Rod's "Wyrmblood" signature -- no memory access.
/// The stateful turn-edge watcher, ally filter, and guarded heals live in Wyrmblood.cs.
/// </summary>
internal sealed partial class Wyrmblood
{
    /// <summary>True when the signature is configured (RegenSplashRadius set) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.RegenSplashRadius > 0;

    /// <summary>Wielder resolution is main-hand-only: the weapon must be in RRHand to activate.
    /// A Living Weapon earns kills in any hand, but commands its gift only from the main hand.</summary>
    public const bool ActivatesOnMainHandOnly = true;

    /// <summary>The wielder's turn edge -- THE rule lives on the shared core
    /// (<see cref="HealPulse.IsTurnEdge"/>; LW-153: this was one of two verbatim copies).
    /// Kept as a delegating alias so the policy surface and its tests read per-module.</summary>
    public static bool IsTurnEdge(int lastTurns, int turns) => HealPulse.IsTurnEdge(lastTurns, turns);

    /// <summary>The per-unit heal: its OWN maxHp / div (vanilla Regen is maxHp/8, integer
    /// floor), floor 1 so tiny units still mend. 0 when maxHp is junk.</summary>
    public static int RegenAmount(int maxHp, int div)
    {
        if (maxHp <= 0 || div <= 0) return 0;
        int heal = maxHp / div;
        return heal < 1 ? 1 : heal;
    }

    /// <summary>True when a unit at (x,y) is inside the splash around the wielder at (wx,wy):
    /// Manhattan distance &lt;= radius (the shared Grid tile math). The wielder itself is distance 0.</summary>
    public static bool InSplash(int wx, int wy, int x, int y, int radius)
        => Grid.Manhattan(wx, wy, x, y) <= radius;
}
