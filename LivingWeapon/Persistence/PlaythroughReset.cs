namespace LivingWeapon;

/// <summary>
/// Detects a fresh playthrough's opening (the Orbonne Prayer new-game dialogue) and
/// resets the kill tally. Only a sustained hold (<see cref="PlaythroughResetPolicy.HoldTicks"/>)
/// fires, so a one-frame EventId dip such as a Continue load cannot reset data. On fire it archives
/// kills.json via <see cref="SaveLocation.Archive"/>, then clears the shared <see cref="KillTally.Kills"/>
/// and saves. legends.json is left alone. Marks are release-hidden.
/// The return value of <see cref="Observe"/> also tells Engine a new game qualified, so it can
/// force BattleState's exit edge (an in-session New Game fires none, leaving a stale KillTracker latch).
/// The data action also needs sensingArmed from LaunchGuard's sensing lane
/// (docs/research/WRITE_SITE_AUDIT_2026-10.md item 3), so a game update that moves the sentinel bytes
/// cannot wipe a real tally. Observe returns <c>Fired</c>, <c>Refused</c> (qualified but unverified,
/// nothing touched) or <c>None</c>. A refusal sets a late-arm latch that fires once if the lane arms
/// during the same hold. Engine runs its state hygiene on Refused as well as Fired.
/// </summary>
internal sealed class PlaythroughReset
{
    private readonly SaveLocation _save;
    private readonly KillTally _tally;
    private int _heldTicks;
    // Late-arm latch. Set by a refused threshold tick, consumed by the first armed tick of
    // the same hold, cleared when the hold breaks.
    private bool _pendingFire;

    public PlaythroughReset(SaveLocation save, KillTally tally)
    {
        _save = save;
        _tally = tally;
    }

    /// <summary>Call once per tick with the values Engine already read. Counts consecutive opening
    /// ticks and resets on any break. At HoldTicks (== so it fires once per hold): with
    /// <paramref name="sensingArmed"/> runs the data action (<see cref="ResetVerdict.Fired"/>), else logs one
    /// refusal and sets the late-arm latch (<see cref="ResetVerdict.Refused"/>). A later armed tick with the
    /// latch set fires once. Both non-None results count as a detection edge, tally empty or not.</summary>
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

/// <summary>what <see cref="PlaythroughReset.Observe"/> decided this tick.</summary>
internal enum ResetVerdict
{
    /// <summary>No detection edge this tick.</summary>
    None,
    /// <summary>The new-game signal qualified and the sentinels are verified. The data action ran.</summary>
    Fired,
    /// <summary>The signal qualified but the sentinel check has not passed. No data was touched.</summary>
    Refused,
}
