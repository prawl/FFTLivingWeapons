namespace LivingWeapon;

/// <summary>
/// The stall half of the main guard. Before it, a mod that could not read the game (the
/// "kill counts never move" reports) retried silently forever. <see cref="LandmarkStall"/> wraps the two
/// main landmarks and calls the handlers below once per long unreadable streak. Also holds the thresholds,
/// copy, latches and the LW_FORCE_GUARD_UNREADABLE blind drill.
/// </summary>
internal sealed partial class LaunchGuard
{
    // Steps are 33 ms Engine ticks (Engine.cs PollMs); real times are approximate.
    /// <summary>1800 steps * 33 ms = 59.4 s. Image (PE) landmark unreadable this long means the mod cannot read the game.</summary>
    internal const int ImageStallSteps = 1800;
    /// <summary>3600 steps * 33 ms = 118.8 s. Roster row empty this long on a readable game is the ordinary title screen.</summary>
    internal const int RosterWaitSteps = 3600;
    /// <summary>2400 steps * 33 ms = 79.2 s. Drill blind window, past the image warning (1800) but short enough that a Continue still arms within the minute.</summary>
    internal const int DrillBlindSteps = 2400;

    // Roster-probe Detail on an Unreadable verdict (LaunchGuard.Landmarks.cs): empty slot (title screen) vs unreadable page.
    internal const string RosterSlotEmpty = "slot empty";
    internal const string RosterPageUnreadable = "page unreadable";

    // Copy approved 2026-10-16. None contains the phrase tools/scan_logs.py reads as a full stand-down ("standing down to protect your save").
    internal const string ImageStallWarn =
        "Living Weapons has not been able to read the game for about a minute, so the checks that need it cannot run and nothing is counting yet. "
        + "It will keep trying. If this message stays and kill counts never move, the mod and the game version probably do not match; see the box for how to get help.";
    internal const string ImageStallBox =
        "Living Weapons cannot find the game yet\n\n"
        + "The mod has been running for a minute but has not been able to read the game's memory where it expects it. "
        + "Your game is fine and nothing has been changed; the mod is just not doing anything yet.\n\n"
        + "This usually means the game updated and the mod needs a matching update. Check the Living Weapons mod page for a newer version.\n\n"
        + "If a newer version does not fix it, email me at ptyrawl@gmail.com and attach livingweapon.log (Open Folder on the mod's page in Reloaded-II). I will sort it out.";
    internal const string RosterWaitInfo =
        "Living Weapons can see the game and is waiting for a save to load before it runs its checks; this is normal on the title screen.";
    internal const string RecoveryInfo =
        "Living Weapons found the game after the earlier warning; all checks passed and it is armed now.";
    private const string DrillSentence =
        " This warning was forced by the LW_FORCE_GUARD_UNREADABLE drill flag (dev builds only).";

    // Once-per-session latches (one LaunchGuard per process); the drill, _blindStepsLeft > 0, makes both main probes Unreadable.
    private bool _stallWarned;
    private bool _waitNoted;
    private readonly bool _forceUnreadable;
    private int _blindStepsLeft;

    /// <summary>Drill decorator inside the stall watcher; only the PE landmark, probed first, spends the blind budget (<paramref name="spends"/>).</summary>
    private GuardLandmark DrillBlind(GuardLandmark inner, bool spends) => new(inner.Name, () =>
    {
        if (_blindStepsLeft > 0)
        {
            if (spends) _blindStepsLeft--;
            return new LandmarkReading(LandmarkVerdict.Unreadable, "drill");
        }
        return inner.Probe();
    });

    private void OnImageStalled(string name, string? detail)
    {
        if (_stallWarned) return;
        _stallWarned = true;
        string drill = _forceUnreadable ? DrillSentence : "";
        _recorder?.Invoke("guard", $"stalled ({name} unreadable for about a minute)");
        // Warn tier, no flush request. Nothing stood down, the log line is the evidence.
        ModLogger.Warn(LogVerb.Startup, ImageStallWarn + drill);
        _notice?.Invoke("FFT Living Weapons", ImageStallBox + drill);
    }

    private void OnRosterStalled(string name, string? detail)
    {
        if (_stallWarned) return;
        if (detail == RosterPageUnreadable) { OnImageStalled(name, detail); return; }
        if (_waitNoted) return;
        _waitNoted = true;
        ModLogger.Event(LogVerb.Startup, RosterWaitInfo);
        _recorder?.Invoke("guard", "waiting for a save to load");
    }

    private void LogRecoveryIfStalled()
    {
        if (!_stallWarned) return;
        ModLogger.Event(LogVerb.Startup, RecoveryInfo);
        _recorder?.Invoke("guard", "armed after the stall warning");
    }
}
