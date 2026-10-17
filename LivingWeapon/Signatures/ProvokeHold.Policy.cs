namespace LivingWeapon;

/// <summary>
/// Pure decisions behind the Provoke hold (release priority, hide/reveal, turn edge, watchdog, guarded
/// bit writers), unit-tested directly. Same split as FeignDeath.Policy.cs; ProvokeHold.cs reads facts,
/// calls these, and applies the result.
/// </summary>
internal sealed partial class ProvokeHold
{
    /// <summary>Why the hold let go, in priority order with Watchdog last (AC 17).</summary>
    internal enum Release { None, BearerGone, BearerDead, EnemyDead, EnemyGone, EnemyDisabled, EnemyTurnDone, Watchdog }

    /// <summary>What to do to every player-side-but-bearer unit this tick.</summary>
    internal enum HideAction { Hide, Reveal }

    /// <summary>The armed release decision for one tick. First match wins. Bearer safety (BearerGone,
    /// BearerDead), then enemy state, then Watchdog last so a stuck hold names its real reason (AC 17).
    /// A transient locate miss (<paramref name="markedMissedOut"/> and <paramref name="markedLocated"/> both
    /// false) falls through; the debounce (decision 11) is in the caller's miss-tick counting.</summary>
    internal static Release ReleaseReason(bool bearerPresent, bool bearerAlive, bool markedLocated,
        bool markedDead, bool markedMissedOut, bool markedDisabled, int markedTurns, int provokeTurns,
        bool watchdogElapsed)
    {
        if (!bearerPresent) return Release.BearerGone;
        if (!bearerAlive) return Release.BearerDead;
        if (markedLocated && markedDead) return Release.EnemyDead;
        if (markedMissedOut) return Release.EnemyGone;
        if (markedLocated && markedDisabled) return Release.EnemyDisabled;
        if (markedTurns >= provokeTurns) return Release.EnemyTurnDone;
        if (watchdogElapsed) return Release.Watchdog;
        return Release.None;
    }

    /// <summary>the hide/reveal decision for one armed tick, first match wins. Hide is the default
    /// and a reveal has to be earned. Re-ordered after the 2026-07-27 live pass failed both casts (see
    /// <see cref="ProvokeHoldTests.Marked_enemy_next_stays_hidden_even_on_a_player_turn_live_2026_07_27"/>),
    /// then given the engagement latch after cast 3 (see
    /// <see cref="ProvokeHoldTests.Marked_enemys_own_ct_payment_does_not_reveal_the_party_live_2026_07_27_cast3"/>).
    ///
    ///   0. <paramref name="engaged"/> -&gt; Hide. CT is paid at turn start, so when the marked enemy's turn
    ///      opens its CT drops and TryEta reads it as far off until the actor pointer catches up. The
    ///      stateful caller sets the latch once the hold hid for next/acting (ProvokeHold._engaged).
    ///   1. <paramref name="markedIsActor"/> -&gt; Hide (caller already gated on not a player turn, see
    ///      ProvokeHold.MarkedIsActor and TickArmed's markedActive).
    ///   2. <paramref name="markedIsNext"/> -&gt; Hide (TurnOrder.TryNextEnemyToAct). Above
    ///      playerSideOwnsTurn because the tape showed a player seat's turn revealing the party while the
    ///      marked enemy was still next, and the AI then committed against a visible party.
    ///   3. <paramref name="playerSideOwnsTurn"/> -&gt; Reveal (criterion 5).
    ///   4. <paramref name="markedIsFarOff"/> -&gt; Reveal when clearly further than the leading enemy by
    ///      Tuning.ProvokeRevealMarginTicks, so a one-position ordering error stays hidden.
    ///   5. otherwise -&gt; Hide (unusable CT/Speed read, a close call, or no enemy candidate).
    ///
    /// Moving branch 3 below 1 and 2 is safe because docs/LIVE_LEDGER.md records (2026-07-22) that a
    /// flagged (Invisible-bit) ally can still be targeted. The only cost is the status icon over the
    /// party, an open cosmetic gap under criterion 18.
    ///
    /// Player-side ownership comes from the per-unit turn flag (<see cref="PlayerSideOwnsTurn"/>),
    /// never a cursor field. It used to read Offsets.TqTeam, which is PLAYER while an enemy targets one, so
    /// the hold hid nobody for the whole enemy turn. PlayerSideOwnsTurn fails open (nobody owns it, or two
    /// seats disagree, returns false), so every ambiguity ends at branch 5's Hide.</summary>
    internal static HideAction ActionFor(bool engaged, bool playerSideOwnsTurn, bool markedIsActor,
        bool markedIsNext, bool markedIsFarOff)
        => ActionForWithReason(engaged, playerSideOwnsTurn, markedIsActor, markedIsNext, markedIsFarOff).action;

    /// <summary>ActionFor plus a tag naming which branch decided. The live pass needs to tell a lookahead
    /// that never fired from one that mispredicted, and tests need to tell branch 2 from branch 5 (both Hide;
    /// branch 4 cannot fire when the marked enemy wins TurnOrder's ranking). An engaged Hide reports
    /// "engaged" even when next/actor/far-off is also true. ActionFor calls this, so the branch order lives
    /// in one place.</summary>
    internal static (HideAction action, string reason) ActionForWithReason(bool engaged,
        bool playerSideOwnsTurn, bool markedIsActor, bool markedIsNext, bool markedIsFarOff)
    {
        if (engaged) return (HideAction.Hide, "engaged");
        if (markedIsActor) return (HideAction.Hide, "actor");
        if (markedIsNext) return (HideAction.Hide, "next");
        if (playerSideOwnsTurn) return (HideAction.Reveal, "player-turn");
        if (markedIsFarOff) return (HideAction.Reveal, "far-off");
        return (HideAction.Hide, "fallback");
    }

    /// <summary>Falling edge of the active flag, same as FeignDeath.TurnEnded, applied to the marked
    /// enemy's actor-pointer match gated on an enemy turn (decision 10, ProvokeHold.MarkedIsActor).</summary>
    internal static bool TurnEnded(bool wasActive, bool nowActive) => wasActive && !nowActive;

    /// <summary>Accrue live battle time toward the watchdog cap (decision 11). Paused ticks add nothing,
    /// so a long menu or alt-tab does not burn the clock.</summary>
    internal static double AccrueWatchdog(double liveElapsed, double deltaSeconds, bool paused) =>
        paused ? liveElapsed : liveElapsed + deltaSeconds;

    /// <summary>True once the accrued live time reaches the watchdog cap.</summary>
    internal static bool WatchdogElapsed(double liveElapsed, double capSeconds) => liveElapsed >= capSeconds;

    // ---- guarded writers (tests run them against a PinnedBuf) ----

    /// <summary>OR/AND the Invisible bit (+0x47/0x10) like FeignDeath.SetStatusBit. False only when a needed
    /// write was refused, so the refusal can be logged (AC 17).</summary>
    internal static bool SetInvisible(IGameMemory mem, long entry, bool on) =>
        TrySetBit(mem, entry + Offsets.AInvisible, Offsets.AInvisibleBit, on);

    /// <summary>Scrub the id-0 mark from both layers, composed first then inflicted (criterion 3b). Mask
    /// RMW only, because composed +0x45 also holds Dead/Undead/Jump/Charging and KillTracker reads it for
    /// death detection. True only when both writes landed or were already clear.</summary>
    internal static bool ClearMark(IGameMemory mem, long entry)
    {
        int by = StatusApply.StatusByte(MarkId);
        byte mask = StatusApply.StatusMask(MarkId);
        bool composed = TrySetBit(mem, entry + StatusApply.Composed + by, mask, false);
        bool inflicted = TrySetBit(mem, entry + StatusApply.Inflicted + by, mask, false);
        return composed && inflicted;
    }

    /// <summary>Read-only test of the Invisible bit, to detect FeignDeath's own hold, which Provoke must
    /// never set or clear (criterion 11).</summary>
    internal static bool HasInvisible(IGameMemory mem, long entry) =>
        (mem.U8(entry + Offsets.AInvisible) & Offsets.AInvisibleBit) != 0;

    /// <summary>Guarded OR/AND of one status bit. True iff the byte reads the wanted state afterward;
    /// false only when a needed write was refused (Writable false).</summary>
    private static bool TrySetBit(IGameMemory mem, long addr, int mask, bool on)
    {
        int cur = mem.U8(addr);
        int want = on ? (cur | mask) : (cur & ~mask);
        if (cur == want) return true;
        if (!mem.Writable(addr, 1)) return false;
        mem.W8(addr, (byte)want);
        return true;
    }
}
