namespace LivingWeapon;

/// <summary>Pure decisions for Ame-no-Murakumo's "Iai" opening-turn Speed hold.
/// No memory access. The stateful half (wielder resolve, hold state, field-max scan,
/// release) lives in Iai.cs.</summary>
internal sealed partial class Iai
{
    /// <summary>Hold target is the higher of the wielder's natural Speed and fieldMax+margin, so
    /// a naturally faster wielder is never slowed. The margin keeps the post-turn refill slow
    /// enough for the 33 ms poll to revert before a second turn is granted (a flat 99 refills
    /// every ~30 ms and cannot be won).</summary>
    internal static int Target(int natural, int fieldMax, int margin)
        => natural > fieldMax ? natural : fieldMax + margin;

    /// <summary>True when the tier qualifies the weapon for the Speed hold this tick.</summary>
    internal static bool ShouldHold(int tier, int atTier) => tier >= atTier;

    /// <summary>Release decision keyed on band-entry address, rebuilt 2026-07-01 on the engine's
    /// ActorPtr global (replaced the deleted own-CT detector, ClassifyCt). Fallback path, used
    /// only when a hold's roster-nameId capture failed (Wielder.RosterNameId returned zero or
    /// negative); the primary path is <see cref="ReleaseSignalById"/>. Fires on either
    ///   arrival, the resolved acting entry changes to the wielder's entry. Covers wait-only
    ///     turns, where no Acted edge fires. Releasing at turn start is safe because the opening
    ///     turn is already scheduled; reverting Speed only affects next-turn scheduling.
    ///   acted edge, Acted rises 0-&gt;1 while the acting entry already equals the wielder's.
    ///     Covers the stale-equal case, where ActorPtr persists across battles and seat
    ///     addresses recur, so no arrival ever fires but the first real action does.
    /// Both are backstopped by the wall-clock cap (Tuning.IaiHoldCapSeconds) in Iai.cs.
    ///
    /// Priming rule. The caller must seed <paramref name="prevActing"/> and
    /// <paramref name="prevActed"/> to the current values on the first evaluated tick without
    /// calling this method. Priming only prevActing leaves prevActed false, which reads as a
    /// false S2 edge when that tick lands mid-action, and releases instantly.
    ///
    /// Known gaps, all bounded by the cap.
    ///   - Stale-equal pointer and a wait-only opening fire neither S1 nor S2.
    ///   - A tick where the wielder resolve returns nothing was closed 2026-07-01. Release and cap
    ///     now run over every unreleased HoldState in Iai.cs and use the hold's captured
    ///     LastEntry when the identity path is unavailable. That gap was the root cause of the
    ///     mirror-churn starvation that <see cref="ReleaseSignalById"/> mainly fixes.
    ///   - If Wielder.Locate returns a frozen (0,0) twin, wielderEntry never matches curActing.
    ///     Rare, the unit would have to stand on tile (0,0).
    ///
    /// Legacy fall-through lane. Consulted only when
    /// <see cref="FlagCorroboration"/> (against Band.FlagOwner) is Indeterminate; see
    /// <see cref="ReleaseDecision"/>.</summary>
    internal static bool ReleaseSignal(long prevActing, long curActing, bool prevActed, bool curActed, long wielderEntry)
        => curActing == wielderEntry && curActing != 0 && (curActing != prevActing || (curActed && !prevActed));

    /// <summary>Primary release decision (2026-07-01), matched on the acting entry's frame nameId
    /// back-reference (Offsets.ANameId) against the hold's roster-nameId capture
    /// (Wielder.RosterNameId) instead of stored band-entry addresses. A revolving engine mirror
    /// frame can make Wielder.Locate bail on the wielder mid-battle, but the acting pointer
    /// always names the real frame and its nameId never changes. Same S1/S2 shape as
    /// <see cref="ReleaseSignal"/>, see its doc for the priming rule and gaps.
    ///
    /// The <paramref name="holdNameId"/> &gt; 0 guard stops a failed capture (-1, or a roster slot
    /// whose nameId reads 0) from matching an equally invalid acting read of 0. A failed capture
    /// sends the caller to the <see cref="ReleaseSignal"/> fallback (Iai.cs).
    /// Also a legacy lane (2026-07-11), see <see cref="ReleaseDecision"/>.</summary>
    internal static bool ReleaseSignalById(long prevActing, long curActing, bool prevActed,
        bool curActed, int actingNameId, int holdNameId)
        => holdNameId > 0 && actingNameId == holdNameId && curActing != 0
           && (curActing != prevActing || (curActed && !prevActed));

    /// <summary>Verdict on the turn-flags check (Band.FlagOwner). Confirm means the
    /// flag owner is this hold's wielder and the hold releases. Refuse means the owner is a
    /// different unit and the hold stays, which is the parked-pointer fix. Indeterminate means the
    /// read did not resolve or the identity cannot be compared, and the legacy signal decides.</summary>
    internal enum FlagVerdict { Confirm, Refuse, Indeterminate }

    /// <summary>Verdict function over Band.FlagOwner, the per-unit PSX turn flag that names
    /// whichever unit's turn is open and so is immune to the parked-pointer bug.
    /// <paramref name="ownerResolved"/> false is Indeterminate (Band.FlagOwner folds a zero-t
    /// battle-opening record and a disagreeing multiple into false, and neither may starve a
    /// release the legacy lane would grant). An owner whose identity cannot be compared
    /// (<paramref name="ownerIdentityKnown"/> false, e.g. a nameId of 0, the same 0==0 trap
    /// <see cref="ReleaseSignalById"/> guards) is also Indeterminate.</summary>
    internal static FlagVerdict FlagCorroboration(bool ownerResolved, bool ownerIdentityKnown, bool ownerIsWielder)
    {
        if (!ownerResolved) return FlagVerdict.Indeterminate;
        if (!ownerIdentityKnown) return FlagVerdict.Indeterminate;
        return ownerIsWielder ? FlagVerdict.Confirm : FlagVerdict.Refuse;
    }

    /// <summary>Release composer. Confirm releases regardless of the legacy signal. Refuse
    /// blocks the release even when the legacy signal fired, so a parked-pointer arrival during
    /// another unit's open turn cannot release. Indeterminate passes the legacy signal through.
    /// The wall-clock cap (Tuning.IaiHoldCapSeconds, Iai.cs) backstops all three.</summary>
    internal static bool ReleaseDecision(bool legacySignal, FlagVerdict flags)
        => flags == FlagVerdict.Confirm || (flags == FlagVerdict.Indeterminate && legacySignal);

    /// <summary>Extracted from Tick's post-release corrective hold. Does the current Speed byte
    /// look like our own boost coming back (the theory, the engine's per-turn normalize
    /// re-paints a baseline that captured the hold), so it should be reset to natural? Checks
    /// the restart residue (<paramref name="bakedResidue"/>) and the hold's last written target
    /// (<paramref name="lastTarget"/>).
    ///
    /// The two arms are asymmetric. BakedResidue has no "cur != natural" guard and LastTarget
    /// does, copied from the original inline expression in Iai.cs. IaiTests pins the collision
    /// that tells them apart, so do not add the missing guard. The caller's write guard
    /// (<c>state.NaturalSpeed &gt;= SpeedSaneMin</c>) stays at the call site in Iai.cs.</summary>
    internal static bool OursResidue(int cur, int bakedResidue, int lastTarget, int naturalSpeed)
        => (bakedResidue > 0 && cur == bakedResidue)
        || (lastTarget > 0 && cur == lastTarget && cur != naturalSpeed);

    /// <summary>Extracted from Tick's release+cap pass. Picks the restore address and returns the
    /// <c>confirmed</c> flag the caller logs ("released by the turn flags" vs "the actor
    /// pointer"). Same logic as the original inline expression in Iai.cs:
    /// <code>
    ///   bool confirmed = released &amp;&amp; flags == FlagVerdict.Confirm;
    ///   long restoreAddr = confirmed ? flagEntry + Offsets.ASpeed
    ///                     : (released &amp;&amp; identity) ? acting + Offsets.ASpeed
    ///                     : lastEntry != 0 ? lastEntry + Offsets.ASpeed : 0;
    /// </code>
    /// Arms in priority order.
    ///   1. Confirmed release restores to the flag owner's entry, since the pointer may be parked
    ///      on another unit at that instant.
    ///   2. Legacy identity release restores to the pointer's acting entry.
    ///   3. Anything else (address-fallback release, or the wall-clock cap with released == false)
    ///      restores best-effort to the hold's last entry. A lastEntry of 0 returns addr 0, skip the write.
    ///
    /// <c>confirmed</c> is folded with <paramref name="released"/> so a cap-fired tick
    /// (released == false) never takes the Confirm arm and lands on the lastEntry arm, even if
    /// <paramref name="flags"/> still reads Confirm.</summary>
    internal static (long addr, bool confirmed) RestoreAddress(
        bool released, bool identity, FlagVerdict flags, long flagEntry, long acting, long lastEntry)
    {
        bool confirmed = released && flags == FlagVerdict.Confirm;
        long addr = confirmed ? flagEntry + Offsets.ASpeed
                  : (released && identity) ? acting + Offsets.ASpeed
                  : lastEntry != 0 ? lastEntry + Offsets.ASpeed : 0;
        return (addr, confirmed);
    }
}
