using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The band door and the static array door, two guards that only step inside a live battle once the roster
/// door is open. A match arms on any live frame. A mismatch counts only on field frames after a grace.
/// The probes live in LaunchGuard.BattleProbes.cs.
/// </summary>
internal sealed partial class LaunchGuard
{
    /// <summary>About twenty seconds of the player's cursor on a live field, counted across battles.</summary>
    internal const int BattleGraceSteps = 600;
    internal const string BattleWaitingReason = "waiting for the first battle";
    internal const string BattleCheckingReason = "checking the battle data";
    internal const string ArrayFailedFirstReason = "the live battle unit data check failed first";
    internal const string BandClosedReason = "the live battle unit data is not where this mod expects it";
    internal const string ArrayClosedReason = "the battle unit list is not where this mod expects it";
    private const string BattleGrace = "battle grace";
    private const string BandDrillDetail = "drill: " + DrillTrigger.BandFlagName;

    private readonly FingerprintGuard _bandGuard;
    private readonly FingerprintGuard _arrayGuard;
    private readonly bool _forceBandMismatch;
    private int _fieldSteps;
    private bool _stepOnField;
    private bool _battleStepped;
    private List<BattleRegionShape.Unit>? _stepBand;
    private bool _bandGraceNoted;
    private bool _arrayGraceNoted;

    internal GuardState BandState => _bandGuard.State;

    internal GuardState StaticArrayState => _arrayGuard.State;

    internal DoorReading BandDoor => _bandGuard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Failed(BandClosedReason
            + (_forceBandMismatch ? ", forced by the " + DrillTrigger.BandFlagName + " drill" : "")),
        _ => DoorReading.Waiting(WaitingReason),
    };

    internal DoorReading StaticArrayDoor => _arrayGuard.State switch
    {
        GuardState.Armed => DoorReading.Open(),
        GuardState.StoodDown => DoorReading.Failed(ArrayClosedReason),
        _ => DoorReading.Waiting(_bandGuard.State == GuardState.StoodDown ? ArrayFailedFirstReason : WaitingReason),
    };

    private string WaitingReason => _battleStepped ? BattleCheckingReason : BattleWaitingReason;

    /// <summary>Engine calls this once per tick after the battle state is known. The band is read and parsed once
    /// per step for both probes, because a guard never runs its probe again once it is terminal.</summary>
    public void StepBattleDoors(bool inLive, bool onField)
    {
        if (!inLive || !_guard.Armed) return;
        bool bandWork = _bandGuard.State == GuardState.Verifying;
        bool arrayWork = _arrayGuard.State == GuardState.Verifying && _bandGuard.State != GuardState.StoodDown;
        if (!bandWork && !arrayWork) return;

        _battleStepped = true;
        _stepOnField = onField;
        if (onField) _fieldSteps++;
        _stepBand = _mem.TryReadBytes(Offsets.FrameReadBase, Offsets.BandSlots * Offsets.CombatStride, out var frames)
            ? BattleRegionShape.ParseBand(frames) : null;
        _bandGuard.Step();
        if (_bandGuard.State != GuardState.StoodDown) _arrayGuard.Step();
    }

    private GuardLandmark BandLandmark() => BattleGraceHold(
        BandDrill(FingerprintGuard.Custom("band-region", ProbeBandRegion)), band: true);

    private GuardLandmark ArrayLandmark() => BattleGraceHold(
        FingerprintGuard.Custom("static-array", ProbeStaticArray), band: false);

    private GuardLandmark BattleGraceHold(GuardLandmark inner, bool band) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        if (reading.Verdict != LandmarkVerdict.Mismatch || (_stepOnField && _fieldSteps > BattleGraceSteps)) return reading;
        ref bool noted = ref (band ? ref _bandGraceNoted : ref _arrayGraceNoted);
        if (!noted)
        {
            noted = true;
            ModLogger.Debug(LogVerb.Startup, (band ? "The live battle unit data read" : "The battle unit list read")
                + " unexpected values in the first seconds on the field; the verdict is held for now in case the battle is still loading (battle grace).");
        }
        return new LandmarkReading(LandmarkVerdict.Unreadable, BattleGrace);
    });

    private GuardLandmark BandDrill(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        if (!_forceBandMismatch || reading.Verdict == LandmarkVerdict.Unreadable) return reading;
        if (reading.Verdict == LandmarkVerdict.Mismatch)
            return new LandmarkReading(LandmarkVerdict.Mismatch, reading.Detail + "; " + BandDrillDetail);
        return new LandmarkReading(LandmarkVerdict.Mismatch, BandDrillDetail);
    });

    private void BandArmedEdge()
    {
        _recorder?.Invoke("guard", "band door armed (a band entry mirrors its roster row)");
        ModLogger.Event(LogVerb.Startup,
            "The live battle unit data reads as expected; kill counting, stat growth and the band signatures are enabled.");
    }

    private void ArrayArmedEdge()
    {
        _recorder?.Invoke("guard", "static array door armed (the list shares a unit with the band)");
        ModLogger.Event(LogVerb.Startup,
            "The battle unit list reads as expected; kill counting and the band signatures are enabled.");
    }

    private void BandStandDown(string diag)
    {
        _recorder?.Invoke("guard", $"band stand-down ({diag})");
        string drillNote = _forceBandMismatch ? " " + PartsReport.BandDrillSentence : "";
        ModLogger.Warn(LogVerb.Startup,
            $"The live battle unit data does not read the way this mod expects ({diag}). Kill counting, stat growth and the band signatures are switched off for this session so nothing is written to the wrong place; the other parts keep working.{HedgeIfUnknownBuild()}{drillNote}");
        _requestFlush?.Invoke("standdown");
    }

    private void ArrayStandDown(string diag)
    {
        _recorder?.Invoke("guard", $"static array stand-down ({diag})");
        ModLogger.Warn(LogVerb.Startup,
            $"The battle unit list does not read the way this mod expects ({diag}). Kill counting and the band signatures are switched off for this session; stat growth and the other parts keep working.{HedgeIfUnknownBuild()}");
        _requestFlush?.Invoke("standdown");
    }
}
