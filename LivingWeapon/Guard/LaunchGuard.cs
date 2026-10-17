using System;

namespace LivingWeapon;

/// <summary>
/// The LivingWeapon adapter over the portable <see cref="FingerprintGuard"/>. The main guard has one stand-down landmark,
/// a DATA-only one (owner decision. No .text code hash, see docs/RELEASE_SCOPE.md section 7): Ramza's
/// roster row shape at RosterBase slot 0. A mismatch stands the whole mod down for the session. the build stamp (PE
/// TimeDateStamp + SizeOfImage) is read first but only reported (LaunchGuard.Stamp.cs); its slot returns Match on any readable
/// read so an unreadable game still feeds the stall warning (<see cref="LandmarkStall"/>, LaunchGuard.Stall.cs).
/// Before a save loads the roster row reads Unreadable, never Mismatch, so the title screen never stands down. Once armed, a guard
/// never re-verifies, because Barrage legitimately edits the JobCommand region afterward. armed and stand-down edges
/// record into the flight ring, and a stand-down requests a "standdown" flush. lifecycle lives here, the landmark constants
/// and Probe in LaunchGuard.Landmarks.cs; the kit lane in LaunchGuard.KitLane.cs.
/// </summary>
internal sealed partial class LaunchGuard
{
    private readonly IGameMemory _mem;
    private readonly FingerprintGuard _guard;
    // The kit-lane guard (LaunchGuard.KitLane.cs) checks the JobCommand rec 8 / rec 9 ability-byte signature (Barrage's anchor).
    // A mismatch usually means another mod rewrote that table, so it disables only Barrage, Shadow Blade and Provoke and the main
    // guard keeps running. It steps only after the main guard armed; since the stamp is only a report, a lane stand-down
    // may be the game update itself. Probe evidence 2026-07-14, tools/probes/anchorscan_feasibility_probe.py, exe backup file
    // offset 0x67D6DB. The rec8/rec9 pair is file-baked static data, present from module-map time, so the lane needs no boot-window gate.
    private readonly FingerprintGuard _kitLaneGuard;
    private readonly object _hookLock = new();
    private Action? _pendingHookArm;
    private readonly Action<string, string>? _notice;
    // Flight recorder taps; null keeps tests quiet.
    private readonly Action<string, string>? _recorder;
    private readonly Action<string>? _requestFlush;
    // The drill ctor arg, kept for the armed-edge unknown-build report (LaunchGuard.Stamp.cs); Mod.cs compiles it to false outside LWDEV.
    private readonly bool _forceMismatch;
    // The sensing lane, a third instance (LaunchGuard.Sensing.cs), verifies the six battle sentinels once per launch,
    // out of battle only. It gates the new-game detector so a moved sentinel cannot clear kills.json; its stand-down
    // switches only that detector off. Its fields (_sensingGuard, _expectedSlot9, _forceSensingMismatch) are declared there.

    /// <param name="notice">The OS-level stand-down notice; null means no OS notice, so unit tests never pop a message box.
    /// Production wires the real notice from Engine's construction site.</param>
    /// <param name="recorder">the flight ring tap (production. Flight.Record).</param>
    /// <param name="requestFlush">the flight flush request (production. Flight.RequestFlush);
    /// only stand-down calls this, with a dedicated "standdown" trigger.</param>
    /// <param name="forceMismatch">Dev drill (LW_FORCE_FINGERPRINT_MISMATCH): forces the build stamp
    /// to read unknown so the report line and the armed-edge warning can be watched; it can no longer
    /// stand the mod down.</param>
    /// <param name="forceSensingMismatch">Dev drill (LW_FORCE_SENSING_MISMATCH): perturbs
    /// the sensing lane's expected slot9 so healthy memory reads as a mismatch.</param>
    /// <param name="forceUnreadable">Dev drill (LW_FORCE_GUARD_UNREADABLE): both main landmarks
    /// read Unreadable for DrillBlindSteps so the stall warning and the recovery line can be watched.</param>
    public LaunchGuard(IGameMemory mem, bool forceMismatch, Action<string, string>? notice = null,
        Action<string, string>? recorder = null, Action<string>? requestFlush = null,
        bool forceSensingMismatch = false, bool forceUnreadable = false)
    {
        _mem = mem;
        _forceMismatch = forceMismatch;
        _notice = notice;
        _recorder = recorder;
        _requestFlush = requestFlush;
        GuardTryRead tryRead = (long a, int n, out byte[] b) => _mem.TryReadBytes(a, n, out b);

        // Plain bool so Release compiles this path too. Perturbs the expected TimeDateStamp to watch the unknown-build report
        // without a real patch. Only the Mod.StartEngine call site is #if LWDEV.
        uint expectedTimeDateStamp = forceMismatch ? ExpectedTimeDateStamp ^ 1 : ExpectedTimeDateStamp;
        _expectedStampTds = expectedTimeDateStamp;   // the stamp probe (LaunchGuard.Stamp.cs) compares against this
        _stampImageBase = ImageBase.Base;

        // Each main landmark is wrapped so a long unreadable streak speaks once
        // (LaunchGuard.Stall.cs); DrillBlind sits inside the watcher so forced reads count.
        _forceUnreadable = forceUnreadable; _blindStepsLeft = forceUnreadable ? DrillBlindSteps : 0;
        var landmarks = new[]
        {
            LandmarkStall.Watch(DrillBlind(FingerprintGuard.Custom("pe-build-key", ProbeBuildStamp), spends: true),
                ImageStallSteps, OnImageStalled),
            LandmarkStall.Watch(DrillBlind(FingerprintGuard.Custom("ramza-roster-row", ProbeRamzaRosterRow), spends: false),
                RosterWaitSteps, OnRosterStalled),
        };
        _guard = new FingerprintGuard(landmarks, StandDown, ArmedEdge, MismatchDebounce);

        // The kit-lane guard's one landmark (JobCommand rec8/rec9), same debounce; edges in LaunchGuard.KitLane.cs.
        var kitLaneLandmarks = new[] { FingerprintGuard.Custom("jobcommand-table", ProbeKitLaneTable) };
        _kitLaneGuard = new FingerprintGuard(kitLaneLandmarks, KitLaneStandDown, KitLaneArmedEdge, MismatchDebounce);

        // The sensing lane's one landmark; lifecycle and probe live in LaunchGuard.Sensing.cs.
        _forceSensingMismatch = forceSensingMismatch;
        _expectedSlot9 = forceSensingMismatch ? SensingSentinelIdle ^ 1 : SensingSentinelIdle;
        _sensingGuard = new FingerprintGuard(new[] { FingerprintGuard.Custom("battle-sentinels", ProbeSensingTuple) },
            SensingStandDown, SensingArmedEdge, MismatchDebounce);
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

    /// <summary>Arms a deferred hook now if the guard is Armed, else stashes it for the Armed edge (once either way;
    /// StoodDown never arms).</summary>
    public void OfferHookArm(Action arm)
    {
        lock (_hookLock)
        {
            if (_guard.State == GuardState.Armed) { arm(); return; }
            _pendingHookArm = arm;
        }
    }

    private void ArmedEdge()
    {
        Mem.WritesEnabled = true;
        // No flush request here; the record rides to the next battle-edge flush.
        _recorder?.Invoke("guard", "armed (all landmarks match; writes enabled)");
        ModLogger.Event(LogVerb.Startup,
            "Every memory landmark matches; Living Weapons is armed (writes were held until now).");
        LogUnknownBuildIfAny();   // one calm warning when the build stamp is not the one this mod was made for
        LogRecoveryIfStalled();   // one extra line only if the stall warning fired earlier
        lock (_hookLock)
        {
            var arm = _pendingHookArm;
            _pendingHookArm = null;
            arm?.Invoke();
        }
    }

    private void StandDown(string diag)
    {
        // Mem.WritesEnabled stays false (Mod.StartEngine set it before construction). record first so a later flush archives why.
        // No drill branch; LW_FORCE_FINGERPRINT_MISMATCH perturbs only the build stamp, which cannot stand the mod down.
        _recorder?.Invoke("guard", $"stand-down ({diag})");
        // The "or another installed mod" clause moved to the kit-lane guard, so this line means a real game-build mismatch.
        ModLogger.Error(LogVerb.Startup,
            $"The game build does not match this mod's memory landmarks ({diag}); Living Weapons is standing down to protect your save. The mod likely needs an update for a new game patch.");
        // Fires once per session (StoodDown is terminal), so a player who never opens the log still learns the mod went inert.
        // Copy authored by the owner after the 2026-07-14 drill review.
        _notice?.Invoke("FFT Living Weapons",
            "Living Weapons has switched itself off\n\n"
            + "The Living Weapons mod checked your game and it does not look like the version "
            + "the mod was built for. This almost always means the game just got an update.\n\n"
            + "To keep your save file safe, the mod has switched itself off until this is "
            + "sorted out. Your game will still run normally, just without Living Weapons "
            + "for now.\n\n"
            + "How to fix it: check the Living Weapons mod page for a newer version. When the "
            + "game updates, the mod usually needs a matching update to catch up.\n\n"
            + "Still stuck? Email me at ptyrawl@gmail.com and I'll help. Please attach your "
            + "logs so I can see what happened. You can get them two ways: copy all the text "
            + "from the black command prompt window that opens when you launch the game, or "
            + "click Open Folder on this mod's page in Reloaded-II and grab the file named "
            + "livingweapon.log.");
        // Requested last and named "standdown": an earlier error may have burnt the "error" FlushOnce latch and
        // battle edges never fire pre-arm (last writer wins on the pending trigger, FlightRecorder.cs).
        _requestFlush?.Invoke("standdown");
    }
}
