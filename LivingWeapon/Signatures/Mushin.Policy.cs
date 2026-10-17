namespace LivingWeapon;

/// <summary>
/// Pure decisions for Kiku-ichimonji's "Mushin" one-shot power charge; no memory access.
/// Mushin.cs tracks the per-wielder turn flag edges and the MOVED/ACTED read, and
/// GrowthEngine.Mushin.cs applies <see cref="PaHeld"/> to the PA lane. A full wait turn arms one
/// charge (0 or 1) and the wielder's next action spends it in a single boosted hit.
/// Round 5 (2026-07-09) dropped the CT-clock apparatus of rounds 2-4 (StacksFromDeltas,
/// ConfirmsPerformance) for the PSX turn-flag mapping documented in Mushin.cs.
/// </summary>
internal static class MushinPolicy
{
    /// <summary>Arms only at the wielder's own turn end (<paramref name="turnEnded"/>) when the
    /// whole turn had neither a move nor an action.</summary>
    internal static bool ShouldArm(bool turnEnded, bool moved, bool acted)
        => turnEnded && !moved && !acted;

    /// <summary>Spends the charge at turn end if the wielder acted, even after moving. Mutually
    /// exclusive with <see cref="ShouldArm"/>. Mushin.cs only clears a charge that was armed.</summary>
    internal static bool ShouldConsume(bool turnEnded, bool acted)
        => turnEnded && acted;

    /// <summary>Zeroes the charge below AtTier, a backstop for the tier gate in Mushin.cs.</summary>
    internal static int EffectiveStacks(int stacks, int tier, int atTier) => tier >= atTier ? stacks : 0;

    /// <summary>The held PA. Zero stacks matches GrowthEngine.WriteTarget's growth formula
    /// (default ToEven rounding, unlike Ultima); each stack adds <paramref name="bonus"/> to the
    /// factor. Clamped to [1,255]. Not capped at the one-shot limit, so tests can use any stack count.</summary>
    internal static int PaHeld(int naturalPa, int tier, double[] factorTable, int stacks, double bonus)
    {
        double factor = factorTable[tier] + stacks * bonus;
        int raw = (int)System.Math.Round(naturalPa * (1 + factor));
        return raw < 1 ? 1 : raw > 255 ? 255 : raw;
    }
}
