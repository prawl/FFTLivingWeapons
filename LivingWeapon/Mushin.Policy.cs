namespace LivingWeapon;

/// <summary>
/// Pure decisions for Kiku-ichimonji's "Mushin" STACKING power boost: no memory access,
/// unit-tested directly. The stateful trigger (turn-window tracking via the ActorPtr fingerprint;
/// act and consume via the TurnQueue fingerprint (Band.ActiveOwner); movement sampling) lives in
/// Mushin.cs; GrowthEngine.Mushin.cs
/// reads the shared stack-count dictionary Mushin.cs writes and applies <see cref="PaHeld"/> to
/// the PA lane.
///
/// Faithful to the "mushin" (no-mind) ideal: a FULL WAIT turn (no move, no action) is the
/// meditative beat that charges the next strike. STACKING (reworked from the one-shot design):
/// each full-wait turn banks ONE stack, up to a caller-supplied max (Tuning.MushinMaxStacks ==
/// 3); a single attack spends every banked stack in one boosted hit, then resets to 0.
/// </summary>
internal static class MushinPolicy
{
    /// <summary>Full-wait-turn predicate at the wielder's own turn END: true only when the whole
    /// turn passed with NEITHER a move NOR an action, a genuine full wait. Either signal alone (a
    /// move-only turn, or an attack) withholds arming. Mid-turn (<paramref name="turnEnded"/>
    /// false) never arms: the decision only fires once the turn has actually closed.</summary>
    internal static bool ShouldArm(bool turnEnded, bool movedDuringTurn, bool actedDuringTurn)
        => turnEnded && !movedDuringTurn && !actedDuringTurn;

    /// <summary>The stack computation: a genuine full-wait turn (<paramref name="shouldArm"/>,
    /// <see cref="ShouldArm"/>'s result) banks one more stack, capped at
    /// <paramref name="maxStacks"/> (idempotent at the cap: waiting again while already at max
    /// simply returns the same count, never an error). Any other turn shape (a move, an attack,
    /// or a turn that never closed) leaves <paramref name="currentStacks"/> untouched -- it is
    /// NOT reset, only an attack's <see cref="ShouldConsume"/> edge ever clears it.</summary>
    internal static int NextStacks(int currentStacks, bool shouldArm, int maxStacks)
        => shouldArm ? (currentStacks + 1 > maxStacks ? maxStacks : currentStacks + 1) : currentStacks;

    /// <summary>Consume decision: every banked stack clears the instant the wielder's OWN acted
    /// edge fires inside its OWN turn window, spending whatever count is currently banked in one
    /// hit. <paramref name="ownTurnActedEdge"/> is already scoped by the caller to "the Acted edge
    /// occurred while the resolved acting entry identity-matched this wielder's own turn"; an edge
    /// outside that window (an enemy's turn, a reaction/counter) must be passed as false, so the
    /// banked stacks survive and the stack computation is unaffected by it either. Zero banked
    /// stacks never "consumes" (nothing to spend, nothing to log).</summary>
    internal static bool ShouldConsume(int stacks, bool ownTurnActedEdge)
        => stacks > 0 && ownTurnActedEdge;

    /// <summary>Gate the banked stacks by tier defensively: even if a caller's bookkeeping somehow
    /// carried stacks &gt; 0 below AtTier (the trigger itself is tier-gated in Mushin.cs, so this
    /// is belt-and-suspenders), the bonus never applies below AtTier.</summary>
    internal static int EffectiveStacks(int stacks, int tier, int atTier) => tier >= atTier ? stacks : 0;

    /// <summary>The held PA for the given natural PA, kill tier, and effective stack count.
    /// ZERO STACKS is BYTE-IDENTICAL to GrowthEngine.WriteTarget's normal growth formula
    /// (round(natural*(1+factor)), default MidpointRounding.ToEven, deliberately NOT Ultima's
    /// AwayFromZero divergence) so a Kiku-ichimonji below tier 3, or between charges, grows
    /// exactly like any other katana; each banked stack adds <paramref name="bonus"/> to that same
    /// factor, additively (N stacks add N x bonus) for the one spent hit. Clamped to [1,255] like
    /// every other stat write.</summary>
    internal static int PaHeld(int naturalPa, int tier, double[] factorTable, int stacks, double bonus)
    {
        double factor = factorTable[tier] + stacks * bonus;
        int raw = (int)System.Math.Round(naturalPa * (1 + factor));
        return raw < 1 ? 1 : raw > 255 ? 255 : raw;
    }
}
