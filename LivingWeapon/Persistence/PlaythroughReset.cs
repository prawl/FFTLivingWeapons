namespace LivingWeapon;

/// <summary>
/// LW-51 Tier-1: detects a fresh playthrough's opening (the Orbonne Prayer new-game dialogue)
/// and resets the kill tally for it, non-destructively. A single qualifying tick never fires
/// (see <see cref="PlaythroughResetPolicy.HoldTicks"/>); only a SUSTAINED hold does, so a
/// one-frame EventId dip (a Continue load, say) can never trip a data-affecting reset. Reads no
/// memory itself (Engine passes the values it already read this tick); on fire it archives the
/// CURRENT kills.json via <see cref="SaveLocation.Archive"/>, then clears (never replaces) the
/// shared <see cref="KillTally.Kills"/> instance and saves. Scope: kills.json only. legends.json
/// (the Reliquary deed ledger) is a deliberate, currently-safe consistency follow-on: Marks are
/// release-hidden on every surface (LW-35), so a stale legends.json carries no visible drift yet.
///
/// LW-56: <see cref="Observe"/>'s return value is a SECOND, independent signal: the "a new game
/// just qualified" detection edge, consumed by Engine to force BattleState's own exit edge (state
/// hygiene). An in-session New Game never fires an ordinary battle-exit edge on its own (the
/// new-game/prologue event ids are real events, and real events suspend the exit debounce), so
/// without a forced exit a stale per-battle attribution latch (KillTracker's weapon latch) would
/// survive from the previous battle straight into the Orbonne opener. The detection edge is
/// deliberately NOT gated on the tally being non-empty: a zero-credit battle followed by a new
/// game still needs the forced exit, even though there is nothing to archive.
///
/// LW-395: the data action (archive + clear) is additionally gated on <c>sensingArmed</c>, the
/// LaunchGuard sensing lane's verdict that the battle sentinels read the way this game build is
/// known to hold them (docs/research/WRITE_SITE_AUDIT_2026-10.md item 3: a game update that moves
/// those bytes could otherwise look like a New Game and wipe a real tally). <see cref="Observe"/>
/// therefore returns a tri-state <see cref="ResetVerdict"/>: <c>Fired</c> (the data action ran),
/// <c>Refused</c> (the signal qualified but the lane has not verified, so NOTHING was archived or
/// cleared), <c>None</c>. A refusal sets a late-arm latch: if the lane arms while the same hold is
/// still going, the first armed held tick fires it once; a hold break clears the latch. Engine still
/// runs its state hygiene (forced battle exit, display and toast resets) on Refused as well as
/// Fired, because the same tick already trusts these sentinel reads for every write gate; only
/// the data consumer waits for Fired.
/// </summary>
internal sealed class PlaythroughReset
{
    private readonly SaveLocation _save;
    private readonly KillTally _tally;
    private int _heldTicks;
    // LW-395 late-arm latch: set when the threshold tick was refused, consumed by the first later
    // armed held tick of the SAME hold, cleared by a hold break.
    private bool _pendingFire;

    public PlaythroughReset(SaveLocation save, KillTally tally)
    {
        _save = save;
        _tally = tally;
    }

    /// <summary>Call once per engine tick with the values Engine.Tick already read this tick.
    /// Increments the hold counter while the opening condition holds, resets it to 0 the instant
    /// it doesn't (the counter counts the same whether or not sensing is armed). On the tick the
    /// counter reaches HoldTicks (== not >=, so a held-and-still-elevated counter never re-fires
    /// on every later tick; a genuine re-fire needs the condition to break and climb back from
    /// scratch): with <paramref name="sensingArmed"/> the data action runs (archive + clear, only
    /// when the tally is non-empty) and the result is <see cref="ResetVerdict.Fired"/>; without it
    /// ONE refusal line is logged, nothing is touched, the late-arm latch is set and the result is
    /// <see cref="ResetVerdict.Refused"/>. On a later held tick, an armed lane with the latch set
    /// runs the data action once (Fired). Both non-None results are the "a new game just qualified"
    /// detection edge (see this class's own doc comment), tally empty or not.</summary>
    public ResetVerdict Observe(int eventId, int battleMode, bool inLive, bool sensingArmed)
    {
        if (PlaythroughResetPolicy.IsOpeningOutOfBattle(eventId, battleMode, inLive)) _heldTicks++;
        else { _heldTicks = 0; _pendingFire = false; }

        if (_heldTicks == PlaythroughResetPolicy.HoldTicks)
        {
            if (sensingArmed) { RunDataAction(); return ResetVerdict.Fired; }
            ModLogger.Warn(LogVerb.Save,
                "A new game signal was seen but the battle sentinel check has not passed, so the kill tally was left untouched (nothing archived, nothing cleared).");
            _pendingFire = true;
            return ResetVerdict.Refused;
        }

        if (_heldTicks > PlaythroughResetPolicy.HoldTicks && _pendingFire && sensingArmed)
        {
            _pendingFire = false;
            RunDataAction();
            return ResetVerdict.Fired;
        }

        return ResetVerdict.None;
    }

    private void RunDataAction()
    {
        if (_tally.Kills.Count == 0) return;
        _save.Archive("kills.json");
        _tally.Kills.Clear();
        _tally.Save();
        ModLogger.Event(LogVerb.Save, "A new game was detected; the previous kill tally was archived and reset.");
    }
}

/// <summary>LW-395: what <see cref="PlaythroughReset.Observe"/> decided this tick.</summary>
internal enum ResetVerdict
{
    /// <summary>No detection edge this tick.</summary>
    None,
    /// <summary>The new-game signal qualified AND the sentinels are verified: the data action ran.</summary>
    Fired,
    /// <summary>The signal qualified but the sentinel check has not passed: no data was touched.</summary>
    Refused,
}
