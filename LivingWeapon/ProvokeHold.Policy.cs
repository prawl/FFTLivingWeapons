namespace LivingWeapon;

/// <summary>
/// The pure decisions behind the Provoke hold -- release priority, the hide/reveal choice, the
/// turn-edge test, the watchdog accumulator, and the guarded status/HP-style bit writers -- with no
/// battle state of their own, so they're unit-tested directly. Mirrors FeignDeath.Policy.cs's split
/// exactly: the stateful orchestrator (ProvokeHold.cs) does nothing but read facts, call these, and
/// apply the actions returned.
/// </summary>
internal sealed partial class ProvokeHold
{
    /// <summary>Why the hold let go. Ordered by <see cref="ReleaseReason"/>'s own priority (safety
    /// first, Watchdog last so a real reason always wins when both are true -- AC 17).</summary>
    internal enum Release { None, BearerGone, BearerDead, EnemyDead, EnemyGone, EnemyDisabled, EnemyTurnDone, Watchdog }

    /// <summary>What to do to every player-side-but-bearer unit this tick.</summary>
    internal enum HideAction { Hide, Reveal }

    /// <summary>The armed release decision for one tick. Priority (first match wins): bearer safety
    /// (BearerGone, BearerDead) beats every enemy-state reason, which beats Watchdog last -- so a
    /// stuck-but-explicable hold always names its REAL reason rather than the watchdog catch-all
    /// (AC 17). EnemyDead/EnemyGone/EnemyDisabled all require the provoked enemy's own state; a
    /// transient locate miss (<paramref name="markedMissedOut"/> false, <paramref name="markedLocated"/>
    /// false) falls through to EnemyTurnDone/Watchdog untouched -- the debounce (decision 11's
    /// sibling) lives in the caller's miss-tick counting, not here.</summary>
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

    /// <summary>LW-127 (D1 revised, RE-ORDERED after the 2026-07-27 live pass failed both casts --
    /// see the session ledger and <see cref="ProvokeHoldTests.Marked_enemy_next_stays_hidden_even_on_a_player_turn_live_2026_07_27"/>,
    /// then GAINED THE ENGAGEMENT LATCH after cast 3's follow-up failure -- see
    /// <see cref="ProvokeHoldTests.Marked_enemys_own_ct_payment_does_not_reveal_the_party_live_2026_07_27_cast3"/>):
    /// the whole hide/reveal decision for one armed tick, evaluated in order -- first match wins.
    /// HIDE IS THE DEFAULT STATE; A REVEAL MUST BE EARNED (branch 5 is the fallback, not merely the
    /// case of an unreadable band walk):
    ///
    ///   0. <paramref name="engaged"/>               -&gt; Hide, unconditionally, no matter what the
    ///      other four inputs say (STATE, not a ranking read -- see ProvokeHold._engaged). Cast 3's
    ///      tape: CT is paid at turn START, so the instant the marked enemy's turn opens, its own CT
    ///      drops by the threshold and TryEta misreads it as far off for a few ticks until the actor
    ///      pointer swings onto it -- branch 4 (far-off) was being fooled by the very event it exists
    ///      to wait for. The latch is set by the STATEFUL caller once the hold has genuinely hidden
    ///      because the marked enemy was next or truly acting, and held through exactly that window.
    ///   1. <paramref name="markedIsActor"/>        -&gt; Hide (its own turn; already gated on NOT a
    ///      player turn by the caller, see ProvokeHold.MarkedIsActor/TickArmed's `markedActive`).
    ///   2. <paramref name="markedIsNext"/>          -&gt; Hide (the run-up: hidden before its turn
    ///      opens, TurnOrder.TryNextEnemyToAct's whole point. MOVED ABOVE playerSideOwnsTurn: the
    ///      live tape showed the marked enemy still genuinely next (markedEta == leaderEta) when a
    ///      player-side seat's own turn opened and revealed the party anyway; the player's turn then
    ///      ended, the marked enemy's turn opened immediately, and the AI committed against a fully
    ///      visible party -- the exact race this feature exists to win, lost).
    ///   3. <paramref name="playerSideOwnsTurn"/>  -&gt; Reveal (criterion 5: your menus can target
    ///      your own units).
    ///   4. <paramref name="markedIsFarOff"/>        -&gt; Reveal (the marked enemy is CLEARLY
    ///      further away than the leading enemy -- Tuning.ProvokeRevealMarginTicks is the margin, so
    ///      a one-position ordering error still leaves the party hidden).
    ///   5. anything else                            -&gt; Hide (today's whole-phase fallback: an
    ///      unusable CT/Speed read, a close call under the margin, or no enemy candidate at all).
    ///
    /// WHY MOVING BRANCH 3 BELOW 1/2 IS SAFE: criterion 5 exists so the player can still target their
    /// own units during their own turn. It is NOT actually load-bearing for that -- docs/LIVE_LEDGER.md
    /// records a live-confirmed fact (2026-07-22) that a flagged (Invisible-bit) ally CAN still be
    /// targeted normally, so the old reveal-on-player-turn was buying nothing while it was active, and
    /// costing the whole feature whenever it coincided with the marked enemy being next. The only
    /// remaining cost of staying hidden through a player turn is the status icon over the party's
    /// heads, which criterion 18 already accepts as an open, un-fixed cosmetic gap.
    ///
    /// LW-135 history, still relevant to why player-side ownership is read from the per-unit turn
    /// flag (<see cref="PlayerSideOwnsTurn"/>) and never a cursor field: this function used to read
    /// Offsets.TqTeam, the condensed TurnQueue struct's team field. During an enemy's action the
    /// cursor sits on the PLAYER unit being targeted, so that field read PLAYER, this returned
    /// Reveal, and the hold hid NOBODY for the entire enemy turn -- the goaded goblin attacked an
    /// ally exactly as if the mod were absent. PlayerSideOwnsTurn reads no cursor field at all, and
    /// FAILS OPEN (nobody owns the flag, or two seats disagree, both return false), so branch 3 can
    /// only fire on a genuinely positive read; every other ambiguity falls through to branch 5's
    /// Hide default, same bias-to-hidden shape as before.</summary>
    internal static HideAction ActionFor(bool engaged, bool playerSideOwnsTurn, bool markedIsActor,
        bool markedIsNext, bool markedIsFarOff)
        => ActionForWithReason(engaged, playerSideOwnsTurn, markedIsActor, markedIsNext, markedIsFarOff).action;

    /// <summary>ActionFor plus a diagnostic tag naming WHICH branch decided, for things that cannot
    /// be told apart from the HideAction alone (adversarial review, BLOCKER-3 and the T2 SHOULD-FIX;
    /// then cast 3's engagement latch): the owner's live pass needs to tell "the lookahead never
    /// fired" apart from "the lookahead fired but mispredicted", a test needs to tell "branch 2
    /// genuinely fired" apart from "branch 5 coincidentally produced the same Hide" -- which matters
    /// because branch 4 can PROVABLY never fire when the marked enemy genuinely wins TurnOrder's
    /// ranking (its own raw ETA is then always exactly the shared leader ETA, so the margin check
    /// never trips), making branch 2 and branch 5 permanently outcome-identical for a "marked is
    /// next" fixture -- and now the tape needs to tell "engaged is holding the hide through a
    /// misprediction" apart from a genuine why=next/why=actor/why=far-off read, which is exactly why
    /// an engaged Hide reports its OWN reason ("engaged") rather than reusing whatever next/actor/
    /// far-off would have said, even when one of those is ALSO true underneath. Delegates to the
    /// SAME branch order as <see cref="ActionFor"/> (this is the one place that order is written down
    /// -- ActionFor calls back into this, never the reverse) so the reason can never drift from the
    /// real decision.</summary>
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

    /// <summary>One completed turn: WAS the active unit last tick, is NOT now -- the falling edge.
    /// Identical shape to FeignDeath.TurnEnded, reused here against the marked enemy's actor-pointer
    /// identity match gated on an enemy turn (decision 10, see ProvokeHold.MarkedIsActor) instead of
    /// the wielder's active-unit match.</summary>
    internal static bool TurnEnded(bool wasActive, bool nowActive) => wasActive && !nowActive;

    /// <summary>Accrue live battle time toward the watchdog cap (decision 11): add the tick's elapsed
    /// delta only when the tick was UNPAUSED; a paused tick returns <paramref name="liveElapsed"/>
    /// unchanged so a long menu or alt-tab never burns down the clock.</summary>
    internal static double AccrueWatchdog(double liveElapsed, double deltaSeconds, bool paused) =>
        paused ? liveElapsed : liveElapsed + deltaSeconds;

    /// <summary>True once the accrued live time reaches the watchdog cap.</summary>
    internal static bool WatchdogElapsed(double liveElapsed, double capSeconds) => liveElapsed >= capSeconds;

    // ---- guarded writers (exercised against a PinnedBuf in tests; the real RPM/WPM guard runs) ----

    /// <summary>OR/AND the Invisible bit (+0x47/0x10), mirroring FeignDeath.SetStatusBit but
    /// returning whether the byte now reads the wanted state: false ONLY when a NEEDED write was
    /// refused (Writable false), so a refusal can log distinctly instead of being silently
    /// swallowed (AC 17). True (no-op) when the bit already reads the wanted state.</summary>
    internal static bool SetInvisible(IGameMemory mem, long entry, bool on) =>
        TrySetBit(mem, entry + Offsets.AInvisible, Offsets.AInvisibleBit, on);

    /// <summary>Scrub the id-0 mark off BOTH layers, composed FIRST then inflicted (criterion 3b).
    /// Mask-scoped RMW only, never a byte write: composed +0x45 is the SAME byte Dead/Undead/Jump/
    /// Charging live on, and KillTracker reads it for death detection -- a whole-byte write there is
    /// a real kill-attribution bug, not a style nit. Returns true only when BOTH writes landed (or
    /// were already clear); a caller that gets false knows the mark is still live somewhere and may
    /// retry next tick.</summary>
    internal static bool ClearMark(IGameMemory mem, long entry)
    {
        int by = StatusApply.StatusByte(MarkId);
        byte mask = StatusApply.StatusMask(MarkId);
        bool composed = TrySetBit(mem, entry + StatusApply.Composed + by, mask, false);
        bool inflicted = TrySetBit(mem, entry + StatusApply.Inflicted + by, mask, false);
        return composed && inflicted;
    }

    /// <summary>True iff the Invisible bit is currently set (read-only). Used to detect "already
    /// invisible before we ever touched it" -- FeignDeath's own hold, which the Provoke hold must
    /// never set OR clear (criterion 11).</summary>
    internal static bool HasInvisible(IGameMemory mem, long entry) =>
        (mem.U8(entry + Offsets.AInvisible) & Offsets.AInvisibleBit) != 0;

    /// <summary>Guarded OR/AND of a single status bit. Returns true iff the byte reads the wanted
    /// state afterward (no change needed, or the write landed); false only when a NEEDED write was
    /// refused (Writable false on that page).</summary>
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
