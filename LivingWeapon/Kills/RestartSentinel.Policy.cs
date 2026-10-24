namespace LivingWeapon;

/// <summary>
/// Pure decisions for RestartSentinel, tested in RestartSentinelPolicyTests.cs. The stateful half is RestartSentinel.cs.
///
/// Basis is [ledger battle-retry-rewind-fingerprint]. A checkpoint retry rewinds
/// the battle with no exit edge. The actor pointer goes raw-null once per retry (3 of 3 tapes), at
/// or before the restore, for about 125-656ms, and a credited heal from zero revive follows within
/// a few ticks (max observed join 172ms). The constants below add a wide margin to those numbers.
///
/// Plain version, a Retried battle must not pay its kills twice, but an ordinary revive keeps its credit.
/// ShouldOpenLatch skips the grace floor when the revived identity matches, because the ~8.75s off-field
/// wait after a death fires the out-of-live re-arm (~2s) repeatedly, so battle age is ~25-30 ticks, far
/// under GraceTicks, when the revive lands. The tuple alone collides, so the caller also needs a nonzero
/// nameId match ([ledger party-nameid-unique-key]). A retry keeping the nameId is unproven
/// ([ledger retry-preserves-credited-identity]), so some retries may be missed. A miss costs nothing a
/// player feels, a false positive destroys an earned kill.
/// </summary>
internal static class RestartSentinelPolicy
{
    /// <summary>How long the game's "nobody is acting" signal must last before we believe a Retry happened, not a
    /// mid-battle bounce (ActorRegister.cs:74-77, 202-208). Tape nulls lasted 125/656/&gt;=156ms at ~33ms per tick.</summary>
    internal const int NullPersistTicks = 2;

    /// <summary>Ticks after a qualified null in which a credited heal from zero revive may still
    /// join it. Tape max join was 172ms (~6 ticks); this is 5x that.</summary>
    internal const int JoinWindowTicks = 30;

    /// <summary>How long the latch stays open. A fresh qualifying join, or a credited
    /// heal from zero revive while open, restarts the countdown (RestartSentinel.PresentRevive).</summary>
    internal const int LatchTicks = 90;

    /// <summary>How young a battle is too young for the null signal alone to mean a Retry, since a battle opens with a
    /// real null. Below it a matching identity is also needed; the restore hook path ignores it.</summary>
    internal const int GraceTicks = 150;

    /// <summary>How long play can sit off the battlefield before the Retry detector starts fresh. Covers a fast
    /// battle-to-battle transition that fires no ResetBattle, so battle age alone is untrustworthy.</summary>
    internal const int OutOfLiveRearmTicks = 60;

    /// <summary>Ticks after a checkpoint restore in which a credited heal from zero revive still counts as
    /// that Retry's. Two live Retries joined restore to revive in about 73 polls, this is about twice that.</summary>
    internal const int RetryHookJoinTicks = 150;

    /// <summary>True once a raw-null streak has persisted long enough to count as retry evidence
    /// rather than a transient pointer bounce.</summary>
    internal static bool NullQualifies(int nullStreakTicks) => nullStreakTicks >= NullPersistTicks;

    /// <summary>Decides whether a revive looks like a Retry. True when a credited heal from zero revive has a qualified
    /// null within <see cref="JoinWindowTicks"/> before it, and the battle is past grace or the identity matches.</summary>
    internal static bool ShouldOpenLatch(bool wasCredited, bool healedFromZero, bool haveQualifiedNull,
                                          int ticksSinceQualifiedNull, int battleAgeTicks, bool identityMatches)
        // A later null must not count for an earlier revive, so callers pass the null's age as of the event.
        => wasCredited && healedFromZero && haveQualifiedNull
           && ticksSinceQualifiedNull <= JoinWindowTicks
           && (battleAgeTicks > GraceTicks || identityMatches);

    /// <summary>Decides from the game's restore hook whether a revive is a Retry, skipping the grace and identity match.
    /// Every battle edge clears the credited corpses and the hook stamp, so a mid-battle restore can only be a rewind.</summary>
    internal static bool ShouldOpenLatchFromRetryHook(bool wasCredited, bool healedFromZero, bool haveRetryHook,
                                                       int ticksSinceRetryHook)
        => wasCredited && healedFromZero && haveRetryHook && ticksSinceRetryHook <= RetryHookJoinTicks;

    /// <summary>Decides whether to wait for the Retry signal before judging a revive. True when a credited heal from zero
    /// revive arrives mid-null (streak &gt;= 1, below <see cref="NullPersistTicks"/>) and passes the gate of <see cref="ShouldOpenLatch"/>.</summary>
    internal static bool ShouldStash(bool wasCredited, bool healedFromZero, int nullStreakTicks,
                                      int battleAgeTicks, bool identityMatches)
        // A Retry can revive a unit before the null signal is long enough to trust, so the verdict waits.
        // A retry tape showed a 0ms null-revive join, so the revive is presented one tick before the null
        // qualifies and the corpse's re-arm clears its credit evidence a tick later. Tick drains the stash
        // through UncreditKills once the streak qualifies, or drops it if the streak breaks.
        => wasCredited && healedFromZero
           && nullStreakTicks >= 1 && nullStreakTicks < NullPersistTicks
           && (battleAgeTicks > GraceTicks || identityMatches);

    internal static bool SwapIdentityMatches((byte lvl, byte br, byte fa, ushort mhp) credited,
                                              (byte lvl, byte br, byte fa, ushort mhp) presented,
                                              ushort creditedNameId, ushort presentedNameId)
        => credited.lvl == presented.lvl && credited.mhp == presented.mhp
           && creditedNameId != 0 && creditedNameId == presentedNameId;
}
