namespace LivingWeapon;

/// <summary>The pure grant decisions behind the Zwill's extra turn on kill. No memory access, so
/// they are unit-tested directly; the stateful side is in ExtraTurn.cs.</summary>
internal sealed partial class ExtraTurn
{
    internal const int CtOff = 0x25;            // scheduler charge-time in the band-entry frame (combat base+0x41, the field Quick writes)
    internal const int SlamCt = 100;            // ct_probe 11 forced turns/40s. 105 might jump the queue but is untested, so probe before raising
    internal const int FullCt = 100;            // "turn in progress / slam took" threshold
    internal const int ConsumeBelow = 70;       // engine pulled CT under this = a turn of theirs ended
    internal const int TookStreak = 3;          // consecutive >=FullCt reads before took latches, so an oscillating turn-end reset counts once
    internal const double NoSignalSeconds = 12.0;   // release if the hold is unhealthy this long; a healthy hold refreshes the deadline
    internal const double AbsoluteCapSeconds = 90.0; // hard stop per grant (e.g. Stopped killer)
    internal const int ZwillId = 10;            // Zwill Straightblade
    internal const int AtTier = 3;              // the +3 grant tier

    /// <summary>
    /// How many turn-end pull-downs the grant still owes, from two consecutive agreeing pre-slam CT
    /// reads (one read can be torn mid-death-reallocation). Both high means the kill-turn is still
    /// running (Owed, two pull-downs); both low means the credit landed after it (Pinning, one).
    /// Disagreement or an unlocated read keeps sampling.
    /// </summary>
    internal static GrantState? Classify(int prevCt, int ct)
    {
        if (prevCt < 0 || ct < 0) return null;
        bool prevHigh = prevCt >= FullCt, high = ct >= FullCt;
        if (prevHigh != high) return null;
        return high ? GrantState.Owed : GrantState.Pinning;
    }

    /// <summary>
    /// Pull-down detector, fed one CT read per tick before re-slamming. "took" latches after
    /// <see cref="TookStreak"/> full reads; a read under <see cref="ConsumeBelow"/> while took means
    /// the engine pulled a landed slam, so one of the killer's turns ended. Counting resets took, so
    /// an oscillating turn-end reset cannot double-count. The 70..99 band and locate gaps (-1)
    /// leave took alone.
    /// </summary>
    internal static (int streak, bool took, bool pullDown) Observe(int streak, bool took, int ct)
    {
        if (ct >= FullCt)
        {
            streak++;
            return (streak, took || streak >= TookStreak, false);
        }
        if (took && ct >= 0 && ct < ConsumeBelow) return (0, false, true);
        return (0, took, false);
    }

    /// <summary>Count pull-downs. The kill-turn's own end first (if owed), then the bonus turn's end,
    /// which means the extra turn was taken.</summary>
    internal static (GrantState next, bool consumed) Step(GrantState s, bool pullDown)
    {
        return s switch
        {
            GrantState.Owed => pullDown ? (GrantState.Pinning, false) : (GrantState.Owed, false),
            GrantState.Pinning => pullDown ? (GrantState.Idle, true) : (GrantState.Pinning, false),
            _ => (s, false),
        };
    }

    /// <summary>Slam only while a pull-down is owed; arming needs unpolluted pre-slam reads.</summary>
    internal static bool Slams(GrantState s) => s is GrantState.Owed or GrantState.Pinning;

    /// <summary>A healthy hold (CT at/above full) refreshes the no-signal deadline. The window is a
    /// pathology timeout, so a long kill-turn or busy queue must not expire the grant.</summary>
    internal static bool Healthy(int ct) => ct >= FullCt;

    /// <summary>Every release except a consumed bonus restores the killer's CT (one guarded write);
    /// a parked full CT would ghost-grant a turn at the next queue rotation (the old Bug B).</summary>
    internal static bool RestoreCt(ReleaseReason r) => r != ReleaseReason.Consumed;
}

/// <summary>Where a Zwill killer sits in the bonus-turn grant. Arming (locate, classify) -> Owed
/// (kill-turn running) -> Pinning (until the bonus move ends) -> Idle.</summary>
internal enum GrantState { Idle, Arming, Owed, Pinning }

/// <summary>Why a grant ended -- logged, and everything except Consumed restores the parked CT.</summary>
internal enum ReleaseReason { Consumed, NoSignal, AbsoluteCap, KillerDead, BattleReset, GateLost }
