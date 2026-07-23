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

    /// <summary>WINDOW mode's hide/reveal choice (decision 3): Reveal only on a clean, sane
    /// PLAYER(0)/ALLY(2) turn; Hide on TqTeam==1 OR any non-clean/garbage read -- bias-to-hidden, so
    /// an unreadable or transitional queue never leaks a hidden unit's turn. SLICE mode does not call
    /// this: it inlines `markedActive ? Hide : Reveal` in the module (tested via the module facade
    /// test, not here).</summary>
    internal static HideAction ActionFor(bool queueSane, int team) =>
        queueSane && (team == 0 || team == 2) ? HideAction.Reveal : HideAction.Hide;

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
