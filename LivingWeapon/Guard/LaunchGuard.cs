using System;

namespace LivingWeapon;

/// <summary>
/// The LivingWeapon adapter over the portable <see cref="FingerprintGuard"/>. It holds six independent guards and each one
/// feeds a door of the parts registry. The main guard checks Ramza's roster row at RosterBase slot 0, a data only landmark, and
/// its armed edge is what enables Mem writes. The kit lane (LaunchGuard.KitLane.cs) checks the JobCommand table, the sensing lane
/// (LaunchGuard.Sensing.cs) checks the battle sentinels, the twin lane (LaunchGuard.Twin.cs) checks the menu flags, and the band
/// and static array doors (LaunchGuard.BattleDoors.cs) check the two battle regions once a battle is live. The build stamp is
/// only reported (LaunchGuard.Stamp.cs). Before a save loads the roster row reads Unreadable, never Mismatch. Armed and
/// stood down are terminal. Boxes belong to the parts board; door readings and the roster drill live in LaunchGuard.Doors.cs.
/// </summary>
internal sealed partial class LaunchGuard
{
    private readonly IGameMemory _mem;
    private readonly FingerprintGuard _guard;
    private readonly FingerprintGuard _kitLaneGuard;
    // Only the stall box still uses it.
    private readonly Action<string, string>? _notice;
    // Flight recorder taps. Null keeps tests quiet.
    private readonly Action<string, string>? _recorder;
    private readonly Action<string>? _requestFlush;
    // Kept for the armed edge unknown build report (LaunchGuard.Stamp.cs). Mod.cs compiles it to false outside LWDEV.
    private readonly bool _forceMismatch;

    /// <param name="notice">The OS level box, used by the stall warning only. Null means no box, so unit tests never pop one.</param>
    /// <param name="recorder">the flight ring tap (production. Flight.Record).</param>
    /// <param name="requestFlush">the flight flush request (production. Flight.RequestFlush).</param>
    /// <param name="forceMismatch">Dev drill LW_FORCE_FINGERPRINT_MISMATCH. Makes the build stamp read unknown. It cannot stand anything down.</param>
    /// <param name="forceSensingMismatch">Dev drill LW_FORCE_SENSING_MISMATCH. Makes healthy sentinels read as a mismatch.</param>
    /// <param name="forceUnreadable">Dev drill LW_FORCE_GUARD_UNREADABLE. Both main landmarks read Unreadable for DrillBlindSteps.</param>
    /// <param name="forceRosterMismatch">Dev drill LW_FORCE_ROSTER_MISMATCH. A populated healthy roster row reads as a mismatch.</param>
    /// <param name="forceTwinMismatch">Dev drill LW_FORCE_TWIN_MISMATCH. Once a save is loaded the menu flag check reads as a mismatch.</param>
    public LaunchGuard(IGameMemory mem, bool forceMismatch, Action<string, string>? notice = null,
        Action<string, string>? recorder = null, Action<string>? requestFlush = null,
        bool forceSensingMismatch = false, bool forceUnreadable = false, bool forceRosterMismatch = false,
        bool forceTwinMismatch = false, bool forceBandMismatch = false)
    {
        _mem = mem;
        _forceMismatch = forceMismatch;
        _forceRosterMismatch = forceRosterMismatch;
        _forceTwinMismatch = forceTwinMismatch;
        _forceBandMismatch = forceBandMismatch;
        _notice = notice;
        _recorder = recorder;
        _requestFlush = requestFlush;

        // Plain bool so Release compiles this path too. Perturbs the expected TimeDateStamp to watch the unknown build report
        // without a real patch. Only the Mod.StartEngine call site is #if LWDEV.
        uint expectedTimeDateStamp = forceMismatch ? ExpectedTimeDateStamp ^ 1 : ExpectedTimeDateStamp;
        _expectedStampTds = expectedTimeDateStamp;   // the stamp probe (LaunchGuard.Stamp.cs) compares against this
        _stampImageBase = ImageBase.Base;

        // Each main landmark is wrapped so a long unreadable streak speaks once (LaunchGuard.Stall.cs).
        _forceUnreadable = forceUnreadable; _blindStepsLeft = forceUnreadable ? DrillBlindSteps : 0;
        var landmarks = new[]
        {
            LandmarkStall.Watch(DrillBlind(FingerprintGuard.Custom("pe-build-key", ProbeBuildStamp), spends: true),
                ImageStallSteps, OnImageStalled),
            RosterLandmark(),
        };
        _guard = new FingerprintGuard(landmarks, StandDown, ArmedEdge, MismatchDebounce);
        _kitLaneGuard = new FingerprintGuard(new[] { KitLaneLandmark() }, KitLaneStandDown, KitLaneArmedEdge, MismatchDebounce);

        _twinGuard = new FingerprintGuard(new[] { TwinLandmark() }, TwinStandDown, TwinArmedEdge, MismatchDebounce);

        _forceSensingMismatch = forceSensingMismatch;
        _expectedSlot9 = forceSensingMismatch ? SensingSentinelIdle ^ 1 : SensingSentinelIdle;
        _sensingGuard = new FingerprintGuard(new[] { SensingLandmark() }, SensingStandDown, SensingArmedEdge, MismatchDebounce);
        _bandGuard = new FingerprintGuard(new[] { BandLandmark() }, BandStandDown, BandArmedEdge, MismatchDebounce);
        _arrayGuard = new FingerprintGuard(new[] { ArrayLandmark() }, ArrayStandDown, ArrayArmedEdge, MismatchDebounce);
    }

    public bool Armed => _guard.Armed;

    /// <summary>Test/diagnostic seam. The underlying state machine's terminal state, so a test can
    /// tell "still retrying" apart from "permanently stood down" (both read Armed == false).</summary>
    internal GuardState State => _guard.State;

    /// <summary>True once the kit-lane guard has verified the JobCommand table itself;
    /// Engine gates Barrage/Shadow Blade/Provoke's Tick on this, independently of <see cref="Armed"/>.</summary>
    public bool KitLaneArmed => _kitLaneGuard.Armed;

    /// <summary>Test/diagnostic seam, mirrors <see cref="State"/> for the kit-lane guard.</summary>
    internal GuardState KitLaneState => _kitLaneGuard.State;

    public void Step() => _guard.Step();

    private void ArmedEdge()
    {
        Mem.WritesEnabled = true;
        // No flush request here. The record rides to the next battle edge flush.
        _recorder?.Invoke("guard", "armed (all landmarks match; writes enabled)");
        ModLogger.Event(LogVerb.Startup,
            "Every memory landmark matches; Living Weapons is armed (writes were held until now).");
        LogUnknownBuildIfAny();   // one calm warning when the build stamp is not the one this mod was made for
        LogRecoveryIfStalled();   // one extra line only if the stall warning fired earlier
    }

    private void StandDown(string diag)
    {
        // Mem.WritesEnabled stays false (Mod.StartEngine set it before construction). Record first so a later flush archives why.
        _recorder?.Invoke("guard", $"stand-down ({diag})");
        string drillNote = _forceRosterMismatch ? " " + PartsReport.RosterDrillSentence : "";
        ModLogger.Error(LogVerb.Startup,
            $"The game build does not match this mod's memory landmarks ({diag}); the parts that need it are standing down to protect your save; see the Parts report line for what still runs. The mod likely needs an update for a new game patch.{drillNote}");
        // Requested last and named "standdown". An earlier error may have burnt the "error" FlushOnce latch and
        // battle edges never fire pre arm (last writer wins on the pending trigger, FlightRecorder.cs).
        _requestFlush?.Invoke("standdown");
    }
}
