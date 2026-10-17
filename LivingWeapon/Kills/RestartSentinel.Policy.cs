namespace LivingWeapon;

/// <summary>
/// Pure decisions for RestartSentinel, unit-tested in RestartSentinelPolicyTests.cs.
/// RestartSentinel.cs holds the stateful tick and latch.
///
/// Basis is docs/LIVE_LEDGER.md row [battle-retry-rewind-fingerprint]. A checkpoint retry rewinds
/// the battle with no exit edge. The actor pointer goes raw-null once per retry (3 of 3 tapes), at
/// or before the restore, for about 125-656ms, and a credited heal-from-zero revive follows within
/// a few ticks (max observed join 172ms). The constants below add a wide margin to those numbers.
/// </summary>
internal static class RestartSentinelPolicy
{
    /// <summary>Ticks a raw actor-pointer null must persist to count as retry evidence instead of
    /// the mid-battle pointer bounce (ActorRegister.cs:74-77, 202-208). The tape retry nulls lasted
    /// 125/656/&gt;=156ms at ~33ms per tick.</summary>
    internal const int NullPersistTicks = 2;

    /// <summary>Ticks after a qualified null in which a credited heal-from-zero revive may still
    /// join it. Tape max join was 172ms (~6 ticks); this is 5x that.</summary>
    internal const int JoinWindowTicks = 30;

    /// <summary>How long the latch stays open. A fresh qualifying join, or a credited
    /// heal-from-zero revive while open, restarts the countdown (RestartSentinel.PresentRevive).</summary>
    internal const int LatchTicks = 90;

    /// <summary>Battle age (ticks since the sentinel's ResetBattle) below which the latch cannot
    /// open, since a battle's opening idle carries a real actor-pointer null.</summary>
    internal const int GraceTicks = 150;

    /// <summary>Consecutive off-field ticks that close the latch and re-arm the grace. Covers the
    /// starved-bracket hole, where a fast battle-to-battle transition fires no ResetBattle
    /// so battle age alone cannot be trusted.</summary>
    internal const int OutOfLiveRearmTicks = 60;

    /// <summary>True once a raw-null streak has persisted long enough to count as retry evidence
    /// rather than a transient pointer bounce.</summary>
    internal static bool NullQualifies(int nullStreakTicks) => nullStreakTicks >= NullPersistTicks;

    /// <summary>Latch-open decision, per event. True when a credited heal-from-zero revive has a
    /// qualified null at or before it within <see cref="JoinWindowTicks"/>, and either the battle
    /// is past its grace or the revived identity matches. Callers pass the null's age as of this
    /// event, so a later null cannot satisfy an earlier revive.
    ///
    /// The identity exemption (live drill, 2026-08-17) exists because grace alone cannot
    /// separate a retry from the starved-bracket hole. The death to game-over screen sits off field for
    /// ~8.75s, so the out-of-live re-arm (<see cref="OutOfLiveRearmTicks"/>, ~2s) fires repeatedly
    /// and battle age is back to ~25-30 ticks, far under <see cref="GraceTicks"/>, when the revive lands.
    ///
    /// The caller (RestartSentinel.PresentRevive) requires the (lvl,br,fa,maxHp) tuple and a nonzero
    /// nameId to both agree, since docs/LIVE_LEDGER.md row [party-nameid-unique-key] shows the tuple
    /// alone collides (the other identity checks moved off it). The exemption covers the grace
    /// floor only. Whether a retry keeps a revived unit's nameId is unproven
    /// ([retry-preserves-credited-identity]), so some real retries may be missed. A miss costs
    /// nothing a player feels, while a false positive destroys an earned kill.</summary>
    internal static bool ShouldOpenLatch(bool wasCredited, bool healedFromZero, bool haveQualifiedNull,
                                          int ticksSinceQualifiedNull, int battleAgeTicks, bool identityMatches)
        => wasCredited && healedFromZero && haveQualifiedNull
           && ticksSinceQualifiedNull <= JoinWindowTicks
           && (battleAgeTicks > GraceTicks || identityMatches);

    /// <summary>Deferred-verdict decision. Retry A's tape shows a 0ms null-revive join, so
    /// when ScanAlivePath presents the revive the null streak is 1, one short of
    /// <see cref="NullPersistTicks"/>, and the corpse's re-arm clears its credited-weapon evidence a
    /// tick later. True when a credited heal-from-zero revive arrives mid-null (streak &gt;= 1, below
    /// NullPersistTicks) and passes the same grace-or-identity gate as <see cref="ShouldOpenLatch"/>.
    /// It means stash and decide later. RestartSentinel.Tick drains the stash through UncreditKills
    /// once the streak qualifies, or drops it if the streak breaks. An already qualified null goes
    /// to ShouldOpenLatch instead.</summary>
    internal static bool ShouldStash(bool wasCredited, bool healedFromZero, int nullStreakTicks,
                                      int battleAgeTicks, bool identityMatches)
        => wasCredited && healedFromZero
           && nullStreakTicks >= 1 && nullStreakTicks < NullPersistTicks
           && (battleAgeTicks > GraceTicks || identityMatches);
}
