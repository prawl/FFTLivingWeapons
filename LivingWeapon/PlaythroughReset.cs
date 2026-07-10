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
/// </summary>
internal sealed class PlaythroughReset
{
    private readonly SaveLocation _save;
    private readonly KillTally _tally;
    private int _heldTicks;

    public PlaythroughReset(SaveLocation save, KillTally tally)
    {
        _save = save;
        _tally = tally;
    }

    /// <summary>Call once per engine tick with the values Engine.Tick already read this tick.
    /// Increments the hold counter while the opening condition holds, resets it to 0 the instant
    /// it doesn't. Returns true on the tick the counter reaches HoldTicks (not >=, so it never
    /// returns true on every subsequent qualifying tick): a held-and-still-elevated counter past
    /// the threshold returns false on every later tick; a genuine re-fire needs the condition to
    /// break and climb back to the threshold from scratch. The return is the "a new game just
    /// qualified" detection edge (see this class's own doc comment): true exactly on that one
    /// tick, tally empty or not. The archive/clear tally ACTION below keeps its own separate,
    /// unchanged gate: it only runs when the detection edge fires AND the tally is non-empty.</summary>
    public bool Observe(int eventId, int battleMode, bool inLive)
    {
        if (PlaythroughResetPolicy.IsOpeningOutOfBattle(eventId, battleMode, inLive)) _heldTicks++;
        else _heldTicks = 0;

        bool detected = _heldTicks == PlaythroughResetPolicy.HoldTicks;

        if (detected && _tally.Kills.Count > 0)
        {
            _save.Archive("kills.json");
            _tally.Kills.Clear();
            _tally.Save();
            ModLogger.Event(LogVerb.Save, "A new game was detected; the previous kill tally was archived and reset.");
        }

        return detected;
    }
}
