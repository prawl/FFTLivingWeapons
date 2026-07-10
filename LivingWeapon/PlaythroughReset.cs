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
    /// it doesn't. Fires exactly once, on the tick the counter reaches HoldTicks (not >=, so it
    /// never refires on every subsequent qualifying tick): a held-and-still-elevated counter past
    /// the threshold cannot refire because the tally is then empty (the post-reset empty tally is
    /// a second guard); a genuine refire needs the condition to break and climb back to the
    /// threshold from scratch.</summary>
    public void Observe(int eventId, int battleMode, bool inLive)
    {
        if (PlaythroughResetPolicy.IsOpeningOutOfBattle(eventId, battleMode, inLive)) _heldTicks++;
        else _heldTicks = 0;

        if (_heldTicks == PlaythroughResetPolicy.HoldTicks && _tally.Kills.Count > 0)
        {
            _save.Archive("kills.json");
            _tally.Kills.Clear();
            _tally.Save();
            ModLogger.Event(LogVerb.Save, "A new game was detected; the previous kill tally was archived and reset.");
        }
    }
}
