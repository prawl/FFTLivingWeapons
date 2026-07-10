namespace LivingWeapon;

/// <summary>
/// Pure decisions for Kiku-ichimonji's "Mushin" ONE-SHOT power charge: no memory access,
/// unit-tested directly. The stateful trigger (the per-wielder TURN FLAG edge tracking, the
/// falling-edge MOVED/ACTED read) lives in Mushin.cs; GrowthEngine.Mushin.cs reads the shared
/// armed dictionary Mushin.cs writes and applies <see cref="PaHeld"/> to the PA lane.
///
/// ONE-SHOT: a full wait turn (no move, no act) arms ONE charge (armed is 0 or 1, never higher);
/// the wielder's next own action spends it in a single boosted hit.
///
/// ROUND 5 (2026-07-09, owner decision: replace rounds 2-4's CT-clock/seq-gate apparatus with the
/// literal design on the PSX turn-flag mapping, see Mushin.cs's class doc for the full
/// provenance): <see cref="ShouldArm"/> and <see cref="ShouldConsume"/> are back to the ORIGINAL
/// round-1 shapes (a truth table over the wielder's own turn-end plus its own moved/acted
/// bookkeeping), full circle. Rounds 2-4's StacksFromDeltas (the CT-cycle aggregation) and
/// ConfirmsPerformance (the action-record pending-confirm truth table) are retired with them.
/// </summary>
internal static class MushinPolicy
{
    /// <summary>The arming decision, evaluated only at the wielder's own turn-end (the TURN FLAG
    /// falling edge, <paramref name="turnEnded"/>; Mushin.cs never calls this mid-turn). True
    /// only when the whole turn passed with NEITHER a move nor an action: a genuine full wait.
    /// Either signal alone (a move-only turn, or an attack) withholds arming.</summary>
    internal static bool ShouldArm(bool turnEnded, bool moved, bool acted)
        => turnEnded && !moved && !acted;

    /// <summary>The consume decision, evaluated at the same turn-end edge: the charge is spent iff
    /// the wielder ACTED during its own turn, regardless of whether it also moved first (a
    /// move-then-attack turn still consumes, never arms; <see cref="ShouldArm"/> and this are
    /// mutually exclusive by construction: acted true fails ShouldArm's !acted term). Mushin.cs
    /// only actually clears/logs a spend when a charge was armed to begin with; this predicate
    /// itself does not know or care whether one was banked.</summary>
    internal static bool ShouldConsume(bool turnEnded, bool acted)
        => turnEnded && acted;

    /// <summary>Gate the armed charge by tier defensively: even if a caller's bookkeeping somehow
    /// carried a charge below AtTier (the trigger itself is tier-gated in Mushin.cs, so this is
    /// belt-and-suspenders), the bonus never applies below AtTier.</summary>
    internal static int EffectiveStacks(int stacks, int tier, int atTier) => tier >= atTier ? stacks : 0;

    /// <summary>The held PA for the given natural PA, kill tier, and effective stack count.
    /// ZERO STACKS is BYTE-IDENTICAL to GrowthEngine.WriteTarget's normal growth formula
    /// (round(natural*(1+factor)), default MidpointRounding.ToEven, deliberately NOT Ultima's
    /// AwayFromZero divergence) so a Kiku-ichimonji below tier 3, or between charges, grows
    /// exactly like any other katana; each banked stack adds <paramref name="bonus"/> to that same
    /// factor, additively (N stacks add N x bonus) for the one spent hit. Clamped to [1,255] like
    /// every other stat write. Not gated to the one-shot cap itself: a pure formula, exercised at
    /// arbitrary stacks counts in MushinPolicyTests to pin its math independent of the runtime cap.</summary>
    internal static int PaHeld(int naturalPa, int tier, double[] factorTable, int stacks, double bonus)
    {
        double factor = factorTable[tier] + stacks * bonus;
        int raw = (int)System.Math.Round(naturalPa * (1 + factor));
        return raw < 1 ? 1 : raw > 255 ? 255 : raw;
    }
}
