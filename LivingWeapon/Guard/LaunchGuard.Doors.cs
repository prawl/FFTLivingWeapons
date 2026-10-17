using System;

namespace LivingWeapon;

/// <summary>
/// The door side of the four guards. It turns each guard's state into a reading for the parts registry,
/// wraps the lane landmarks with their stall watchers and the sensing boot grace, and holds the roster drill.
/// </summary>
internal sealed partial class LaunchGuard
{
    /// <summary>1800 steps * 33 ms is about a minute. A lane unreadable this long speaks once.</summary>
    internal const int DoorWaitSteps = 1800;
    /// <summary>300 steps * 33 ms is about ten seconds. A sensing mismatch inside it is held, never counted.</summary>
    internal const int SensingBootGraceSteps = 300;
    internal const string SensingBattleLive = "battle live";
    internal const string SensingBootGrace = "boot grace";

    internal const string RosterWaitingReason = "waiting for a save to load";
    internal const string RosterUnreadableReason = "the game cannot be read yet";
    internal const string RosterClosedReason = "the party data is not where this mod expects it";
    internal const string KitLaneWaitingReason = "the job command check has not run yet";
    internal const string KitLaneClosedReason = "another mod rewrote the job command data";
    internal const string SensingWaitingReason = "the battle sentinel check has not run yet";
    internal const string SensingClosedReason = "the battle sentinels read wrong, so only the new game detector is off";
    internal const string TwinFlagsUnreadableReason = "the menu flags cannot be read yet";
    internal const string TwinNotRunReason = "the menu flag check has not run yet";
    internal const string TwinClosedReason = "the menu flags the twin weapon grant relies on do not read as expected";
    private const string RosterDrillDetail = "drill: " + DrillTrigger.RosterFlagName;

    private readonly bool _forceRosterMismatch;
    private string? _rosterDetail;
    private int _sensingSteps;
    private bool _graceNoted;
    private bool _kitLaneStallNoted;
    private bool _sensingStallNoted;

    /// <summary>The unknown build hedge, or empty when the build is known. The parts board appends it to its box.</summary>
    internal string BuildHedge => HedgeIfUnknownBuild();

    internal DoorReading RosterDoor => _guard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Failed(RosterClosedReason
            + (_forceRosterMismatch ? ", forced by the " + DrillTrigger.RosterFlagName + " drill" : "")),
        _ => DoorReading.Waiting(_rosterDetail == RosterPageUnreadable ? RosterUnreadableReason : RosterWaitingReason),
    };

    internal DoorReading KitLaneDoor => _kitLaneGuard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Failed(KitLaneClosedReason),
        _ => DoorReading.Waiting(KitLaneWaitingReason),
    };

    internal DoorReading TwinPistolsDoor => _twinGuard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Failed(TwinClosedReason
            + (_forceTwinMismatch ? ", forced by the " + DrillTrigger.TwinFlagName + " drill" : "")),
        _ => DoorReading.Waiting(_twinDetail switch
        {
            RosterSlotEmpty => RosterWaitingReason,
            RosterPageUnreadable => RosterUnreadableReason,
            TwinFlagsUnreadable => TwinFlagsUnreadableReason,
            _ => TwinNotRunReason,
        }),
    };

    // The sensing closure only switches the new game detector off, so it never counts as a failed check.
    internal DoorReading SensingDoor => _sensingGuard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Absent(SensingClosedReason),
        _ => DoorReading.Waiting(SensingWaitingReason),
    };

    // Outermost to innermost. The stall watcher, the tap that remembers why the row is unreadable, the blind drill,
    // the roster drill, the probe. The drill sits inside the blind so it fires only on a populated row.
    private GuardLandmark RosterLandmark() => LandmarkStall.Watch(
        RosterTap(DrillBlind(RosterDrill(FingerprintGuard.Custom("ramza-roster-row", ProbeRamzaRosterRow)), spends: false)),
        RosterWaitSteps, OnRosterStalled);

    private GuardLandmark RosterTap(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        _rosterDetail = reading.Verdict == LandmarkVerdict.Unreadable ? reading.Detail : null;
        return reading;
    });

    private GuardLandmark RosterDrill(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        if (!_forceRosterMismatch || reading.Verdict == LandmarkVerdict.Unreadable) return reading;
        if (reading.Verdict == LandmarkVerdict.Mismatch)
            return new LandmarkReading(LandmarkVerdict.Mismatch, reading.Detail + "; " + RosterDrillDetail);
        return new LandmarkReading(LandmarkVerdict.Mismatch, RosterDrillDetail);
    });

    private GuardLandmark KitLaneLandmark() => LandmarkStall.Watch(
        FingerprintGuard.Custom("jobcommand-table", ProbeKitLaneTable), DoorWaitSteps, OnKitLaneStalled);

    // Both waits read Unreadable on purpose, so they must not feed the stall timer.
    private GuardLandmark SensingLandmark() => LandmarkStall.Watch(
        SensingGrace(FingerprintGuard.Custom("battle-sentinels", ProbeSensingTuple)), DoorWaitSteps, OnSensingStalled,
        r => r.Detail != SensingBattleLive && r.Detail != SensingBootGrace);

    private GuardLandmark SensingGrace(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        if (reading.Verdict != LandmarkVerdict.Mismatch || _sensingSteps > SensingBootGraceSteps) return reading;
        if (!_graceNoted)
        {
            _graceNoted = true;
            ModLogger.Debug(LogVerb.Startup,
                "The battle sentinels read unexpected values in the first seconds of the launch; the verdict is held for now in case the game is still starting up (boot grace).");
        }
        return new LandmarkReading(LandmarkVerdict.Unreadable, SensingBootGrace);
    });

    private void OnKitLaneStalled(string name, string? detail)
        => NoteDoorStall(ref _kitLaneStallNoted, "job command", "weapon commands stay");

    private void OnSensingStalled(string name, string? detail)
        => NoteDoorStall(ref _sensingStallNoted, "battle sentinel", "battle sensing stays");

    private void NoteDoorStall(ref bool noted, string door, string parts)
    {
        if (noted) return;
        noted = true;
        _recorder?.Invoke("guard", $"stalled ({door} check unreadable for about a minute)");
        ModLogger.Event(LogVerb.Startup,
            $"The {door} check has not been able to read its data for about a minute; {parts} WAITING. It will keep trying.");
    }
}
