namespace LivingWeapon;

/// <summary>The pure grant decisions behind the Zwill's "extra turn on kill" -- no memory access,
/// so they're unit-tested directly. The stateful orchestrator (wielder resolve + locate + CT slam
/// + windows) lives in ExtraTurn.cs.</summary>
internal sealed partial class ExtraTurn
{
    internal const int CtOff = 0x25;            // scheduler charge-time in the BAND-ENTRY frame (== combat
                                                //   base+0x41, the live-proven field Quick writes)
    internal const int SlamCt = 100;            // the live-proven value (ct_probe: 11 forced turns/40s).
                                                //   105 MIGHT jump the queue past other ready units but is
                                                //   unproven -- probe before raising; the no-signal window
                                                //   makes 100 safe (Pinning simply outlasts queue traffic).
    internal const int FullCt = 100;            // "turn in progress / slam took" threshold
    internal const int ConsumeBelow = 70;       // engine pulled CT under this = a turn of theirs ENDED
    internal const int TookStreak = 3;          // consecutive >=FullCt reads before took latches (refractory:
                                                //   a turn-end reset oscillating with our slam stays ONE event)
    internal const double NoSignalSeconds = 12.0;   // release if the hold is unhealthy this long (a healthy
                                                    //   hold -- located, CT full -- refreshes the deadline)
    internal const double AbsoluteCapSeconds = 90.0; // hard stop per grant, however healthy (e.g. Stopped killer)
    internal const int ZwillId = 10;            // Zwill Straightblade
    internal const int AtTier = 3;              // the +3 grant tier

    /// <summary>
    /// How many turn-end pull-downs the grant still owes, decided from TWO consecutive agreeing
    /// pre-slam CT reads (one read can be a torn struct mid-death-reallocation). The engine holds
    /// CT at full while a turn is in progress, so: both high = the kill-turn is still running and
    /// its own end comes first (Owed, two pull-downs); both low = the credit landed after the turn
    /// (Pinning, only the bonus's end remains). Disagreement or an unlocated read keeps sampling.
    /// </summary>
    internal static GrantState? Classify(int prevCt, int ct)
    {
        if (prevCt < 0 || ct < 0) return null;
        bool prevHigh = prevCt >= FullCt, high = ct >= FullCt;
        if (prevHigh != high) return null;
        return high ? GrantState.Owed : GrantState.Pinning;
    }

    /// <summary>
    /// The refractory pull-down detector, fed one CT read per tick (read BEFORE re-slamming, so an
    /// engine write always survives at least one read). "took" latches after <see cref="TookStreak"/>
    /// consecutive full reads; a read under <see cref="ConsumeBelow"/> while took = the engine pulled
    /// a landed slam = one turn of the killer's ENDED (the only thing that pulls CT). Counting an
    /// event resets took, so an oscillating turn-end reset (engine 0 / our 105 / engine 0) cannot
    /// double-count. The wobble band (70..99) and locate gaps (-1) neither count nor clear took.
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

    /// <summary>Count pull-downs: the kill-turn's own end first (if owed), then the bonus turn's
    /// end -- which means the engine consumed our slammed CT and the extra turn was actually taken.</summary>
    internal static (GrantState next, bool consumed) Step(GrantState s, bool pullDown)
    {
        return s switch
        {
            GrantState.Owed    => pullDown ? (GrantState.Pinning, false) : (GrantState.Owed, false),
            GrantState.Pinning => pullDown ? (GrantState.Idle, true) : (GrantState.Pinning, false),
            _                  => (s, false),
        };
    }

    /// <summary>Slam only while a pull-down is owed. Arming must NOT slam: classification needs
    /// unpolluted pre-slam reads.</summary>
    internal static bool Slams(GrantState s) => s is GrantState.Owed or GrantState.Pinning;

    /// <summary>A healthy hold (CT at/above full -- the engine holding a turn, or our slam landed)
    /// refreshes the no-signal deadline: the window is a pathology timeout, NOT a phase budget. A
    /// player deliberating through a long kill-turn, or a busy queue, must never expire the grant.</summary>
    internal static bool Healthy(int ct) => ct >= FullCt;

    /// <summary>Every release except a consumed bonus restores the killer's CT (one guarded write):
    /// a parked full CT would ghost-grant a turn whenever the queue next rotates -- exactly the old
    /// Bug B -- and would poison the next grant's classification.</summary>
    internal static bool RestoreCt(ReleaseReason r) => r != ReleaseReason.Consumed;
}

/// <summary>Where a Zwill killer sits in the bonus-turn grant: Arming (locating + classifying how
/// many pull-downs are owed) -> Owed (kill-turn still running; slam through its end) -> Pinning
/// (slam until the bonus move's own end) -> Idle.</summary>
internal enum GrantState { Idle, Arming, Owed, Pinning }

/// <summary>Why a grant ended -- logged, and everything except Consumed restores the parked CT.</summary>
internal enum ReleaseReason { Consumed, NoSignal, AbsoluteCap, KillerDead, BattleReset, GateLost }
