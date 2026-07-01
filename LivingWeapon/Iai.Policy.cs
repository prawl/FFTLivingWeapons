namespace LivingWeapon;

/// <summary>Pure decisions for Ame-no-Murakumo's "Iai" opening-turn Speed hold.
/// No memory access -- unit-tested directly. The stateful orchestrator (wielder resolve,
/// per-wielder hold state, field-max scan, release) lives in Iai.cs.</summary>
internal sealed partial class Iai
{
    /// <summary>Opening-turn Speed hold target: the higher of the wielder's natural Speed and
    /// fieldMax+margin, so a naturally-faster wielder is never slowed (never lowered) and a
    /// slower one is brought just above the field max. The "+margin" property keeps the
    /// post-turn refill rate slow enough that the 33 ms poll safely reverts before a second
    /// turn can be granted (flat 99 makes the refill race at ~30 ms/refill -- unwinnable).</summary>
    internal static int Target(int natural, int fieldMax, int margin)
        => natural > fieldMax ? natural : fieldMax + margin;

    /// <summary>True when the tier qualifies the weapon for the Speed hold this tick.</summary>
    internal static bool ShouldHold(int tier, int atTier) => tier >= atTier;

    /// <summary>Pure release decision for the opening-turn Speed hold, rebuilt 2026-07-01 on the
    /// engine's own ActorPtr global (positional identity, per band-entry ADDRESS -- replaces the
    /// deleted own-CT pull-down detector, ClassifyCt). FALLBACK PATH (v2, same day): the primary
    /// release decision is now <see cref="ReleaseSignalById"/> (identity-matched via the frame
    /// nameId back-reference); this address form is used only when a hold's roster-nameId
    /// capture failed (Wielder.RosterNameId returned zero or negative) -- degraded v1 behavior,
    /// never worse. Fires on EITHER:
    ///   S1 ARRIVAL -- the resolved acting entry CHANGES to the wielder's own entry (a pointer
    ///     transition). Covers wait-only turns (no Acted edge fires for a move+wait turn -- Acted
    ///     marks ACTIONS). Releasing at turn-START is safe: the opening turn is already
    ///     granted/scheduled by the time the pointer arrives; reverting Speed mid-turn only
    ///     affects NEXT-turn scheduling.
    ///   S2 ACTED-EDGE MATCH -- the Acted flag rises 0-&gt;1 WHILE the resolved acting entry
    ///     already equals the wielder's entry. Covers the stale-equal corner: ActorPtr is a
    ///     global that persists across battles and seat addresses recur, so it can already equal
    ///     the wielder's entry when the battle opens (no arrival transition ever fires) -- but
    ///     its first real ACTION does.
    /// Both are backstopped by the wall-clock cap (Tuning.IaiHoldCapSeconds) in Iai.cs.
    ///
    /// PRIMING CONTRACT (F5, load-bearing): the caller MUST seed <paramref name="prevActing"/>/
    /// <paramref name="prevActed"/> to the CURRENT values on the first evaluated tick WITHOUT
    /// calling this method that tick. A naive implementation that primes only prevActing
    /// (defaulting prevActed=false) sees a FALSE S2 edge when the first evaluated tick lands
    /// mid-action (Acted already 1) with a stale-equal pointer, and releases instantly -- the
    /// exact bug this contract exists to prevent.
    ///
    /// CORNERS (documented, not defended in code -- all cap-bounded):
    ///   - stale-equal AND wait-only opening: neither S1 nor S2 ever fires (no arrival, no acted
    ///     edge); the wall-clock cap terminates the hold.
    ///   - a tick where the wielder resolve returns nothing (mid-animation gap / late arming):
    ///     CLOSED in v2 (2026-07-01) -- release+cap now evaluate over EVERY unreleased HoldState
    ///     in Iai.cs, not just this tick's resolved _wielders, using the hold's captured
    ///     LastEntry as this method's target when the identity path is unavailable; a pointer
    ///     transition is no longer consumed unseen just because Locate missed that tick. This was
    ///     the actual root cause of the mirror-churn starvation the identity path (see
    ///     <see cref="ReleaseSignalById"/>) primarily fixes.
    ///   - reaction: ActorPtr may name the wielder mid-ENEMY-turn if it is reacting, but before
    ///     the wielder's own opening turn no enemy has acted yet (the wielder goes FIRST -- that
    ///     is the signature), so there is nothing to react to; moot after release.
    ///   - (0,0)-twin: if Wielder.Locate returned a frozen (0,0) duplicate for the wielder while
    ///     the engine points at the real entry, wielderEntry never matches curActing; rare (the
    ///     unit would have to stand on literal tile (0,0)).</summary>
    internal static bool ReleaseSignal(long prevActing, long curActing, bool prevActed, bool curActed, long wielderEntry)
        => curActing == wielderEntry && curActing != 0 && (curActing != prevActing || (curActed && !prevActed));

    /// <summary>PRIMARY release decision (v2, 2026-07-01): identity-matched via the acting
    /// entry's frame nameId back-reference (Offsets.ANameId) against a hold's roster-nameId
    /// capture (Wielder.RosterNameId), instead of comparing stored band-entry ADDRESSES. Fixes
    /// the mirror-churn starvation <see cref="ReleaseSignal"/> is exposed to: a revolving engine
    /// MIRROR frame can make Wielder.Locate ambiguity-bail on a wielder mid-battle, so the
    /// wielder's entry ADDRESS is no longer reliably known -- but the acting POINTER always names
    /// the real frame, and the real frame's nameId never changes, so identity-matching it survives
    /// the churn. Same S1 arrival / S2 acted-edge shape as <see cref="ReleaseSignal"/> (see its
    /// doc comment for the priming contract and corners), just keyed on nameId instead of address.
    ///
    /// The <paramref name="holdNameId"/> &gt; 0 guard is LOAD-BEARING: a failed capture
    /// (Wielder.RosterNameId returned -1, or a single ambiguous roster slot whose nameId itself
    /// reads 0/invalid) must NEVER match an equally-invalid acting read of 0 -- the "0==0 trap".
    /// Both sentinels naturally read 0/negative on failure, so without this guard two unrelated
    /// failures would look like a match and fire a spurious release. A failed capture routes the
    /// caller to the <see cref="ReleaseSignal"/> fallback instead (Iai.cs).</summary>
    internal static bool ReleaseSignalById(long prevActing, long curActing, bool prevActed,
        bool curActed, int actingNameId, int holdNameId)
        => holdNameId > 0 && actingNameId == holdNameId && curActing != 0
           && (curActing != prevActing || (curActed && !prevActed));
}
