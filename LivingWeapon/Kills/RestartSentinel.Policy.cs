namespace LivingWeapon;

/// <summary>
/// Pure decisions for RestartSentinel (LW-233): no memory access, no tick bookkeeping, unit-tested
/// directly (RestartSentinelPolicyTests.cs). See RestartSentinel.cs for the stateful tick/latch
/// machinery this drives.
///
/// PROVENANCE (docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint], plan v2 -- adversarially
/// reviewed twice): a checkpoint retry rewinds the battle in place with NO exit edge. The engine
/// actor pointer folds to raw-null exactly once per retry (3/3 tapes), at-or-before the restore,
/// persisting roughly 125-656ms before re-acquiring; every restore also carries at least one
/// CREDITED heal-from-zero revive within a few ticks of that null (tape max observed join 172ms).
/// The constants below turn those tape numbers into debounce/window thresholds with a wide safety
/// margin -- see each constant's own doc for its specific provenance.
/// </summary>
internal static class RestartSentinelPolicy
{
    /// <summary>Consecutive ticks a raw actor-pointer null must persist before it QUALIFIES as
    /// retry evidence, rather than the documented mid-battle pointer-bounce idle-flicker family
    /// (ActorRegister.cs:74-77, 202-208). All three tapes' retry nulls persisted far past this
    /// (125/656/&gt;=156ms at a ~33ms tick, i.e. many ticks each).</summary>
    internal const int NullPersistTicks = 2;

    /// <summary>How many ticks after a qualified null a credited heal-from-zero revive may still
    /// join it. Tape max observed join was 172ms (~6 ticks at 33ms/tick); x5 margin.</summary>
    internal const int JoinWindowTicks = 30;

    /// <summary>How long the latch stays open once opened. Re-openable: a fresh qualifying join
    /// (or any wasCredited+healedFromZero revive presented while still open) restarts the
    /// countdown to the full duration -- see RestartSentinel.PresentRevive.</summary>
    internal const int LatchTicks = 90;

    /// <summary>Battle age (ticks since the sentinel's own ResetBattle) below which the latch may
    /// never open: a battle's OWN opening idle carries a real actor-pointer null (documented in
    /// the premise ledger), and this keeps that from ever being mistaken for a retry.</summary>
    internal const int GraceTicks = 150;

    /// <summary>Consecutive off-field/out-of-live ticks that close the latch and re-arm the
    /// grace: the LW-108 starved-bracket hole -- a battle-to-battle transition fast enough to eat
    /// the normal enter-edge debounce fires no ResetBattle, so battle age alone cannot be trusted
    /// to mean "still the same encounter". A sustained stretch of the sentinel's own out-of-live
    /// input is an independent signal that whatever comes next is not mid-retry.</summary>
    internal const int OutOfLiveRearmTicks = 60;

    /// <summary>True once a raw-null streak has persisted long enough to count as retry evidence
    /// rather than a transient pointer bounce.</summary>
    internal static bool NullQualifies(int nullStreakTicks) => nullStreakTicks >= NullPersistTicks;

    /// <summary>THE LATCH-OPEN DECISION (asymmetric, per-event): true only when a credited
    /// heal-from-zero revive is presented while a qualified null occurred AT OR BEFORE it, within
    /// <see cref="JoinWindowTicks"/>, and the battle is past its own opening grace. A null observed
    /// on a LATER tick can never retroactively satisfy an earlier revive: callers only ever pass
    /// the null's age as of THIS event, so "a null after the revive never opens" falls out
    /// structurally rather than needing its own check here.</summary>
    internal static bool ShouldOpenLatch(bool wasCredited, bool healedFromZero, bool haveQualifiedNull,
                                          int ticksSinceQualifiedNull, int battleAgeTicks)
        => wasCredited && healedFromZero && haveQualifiedNull
           && ticksSinceQualifiedNull <= JoinWindowTicks
           && battleAgeTicks > GraceTicks;

    /// <summary>THE DEFERRED-VERDICT DECISION (LW-233 fix, verifier-caught): retry A's REAL tape
    /// shape is a 0ms null-revive join -- the raw null and the credited corpses' alive-again read
    /// land on the SAME tick, so at the exact tick ScanAlivePath's one-shot revive presentation
    /// runs, the null streak is only 1, one tick short of <see cref="NullPersistTicks"/>. Dropping
    /// that event outright (the pre-fix behavior) loses it forever: a tick later, when the null
    /// DOES qualify, the corpse's re-arm has already cleared its credited-weapon evidence. This
    /// predicate names exactly the deferral window: a credited heal-from-zero revive presented
    /// while a null is CURRENTLY mid-flight (streak &gt;= 1) but has not yet reached
    /// <see cref="NullPersistTicks"/>, past grace. True here means "stash it, do not decide yet" --
    /// RestartSentinel.Tick drains the stash once the streak either reaches NullPersistTicks (drain
    /// through UncreditKills) or breaks first (drop silently, exactly today's ordinary re-arm). An
    /// already-qualified null (streak &gt;= NullPersistTicks) never reaches here: ShouldOpenLatch
    /// handles that case directly, with no need to stash anything.</summary>
    internal static bool ShouldStash(bool wasCredited, bool healedFromZero, int nullStreakTicks, int battleAgeTicks)
        => wasCredited && healedFromZero
           && nullStreakTicks >= 1 && nullStreakTicks < NullPersistTicks
           && battleAgeTicks > GraceTicks;
}
