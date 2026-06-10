namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Dragon Rod's "Wyrmblood" signature -- no memory access.
/// The stateful turn-edge watcher, ally filter, and guarded heals live in Wyrmblood.cs.
/// </summary>
internal sealed partial class Wyrmblood
{
    /// <summary>True when the signature is configured and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
    {
        if (sig is null || sig.RegenSplashRadius <= 0) return false;
        return tier >= sig.AtTier;
    }

    /// <summary>The wielder's turn edge: a PRIMED TurnTracker count climbed. -1 = unprimed
    /// (first sight after a reset or a re-equip baselines silently). A count that DROPPED
    /// (tracker reset under us) re-baselines instead of splashing.</summary>
    public static bool IsTurnEdge(int lastTurns, int turns) => lastTurns >= 0 && turns > lastTurns;

    /// <summary>The per-unit heal: its OWN maxHp / div (vanilla Regen is maxHp/8, integer
    /// floor), floor 1 so tiny units still mend. 0 when maxHp is junk.</summary>
    public static int RegenAmount(int maxHp, int div)
    {
        if (maxHp <= 0 || div <= 0) return 0;
        int heal = maxHp / div;
        return heal < 1 ? 1 : heal;
    }

    /// <summary>True when a unit at (x,y) is inside the splash around the wielder at (wx,wy):
    /// Manhattan distance &lt;= radius (Ricochet tile math). The wielder itself is distance 0.</summary>
    public static bool InSplash(int wx, int wy, int x, int y, int radius)
        => Ricochet.Manhattan(wx, wy, x, y) <= radius;
}
