using System;

namespace LivingWeapon;

/// <summary>
/// LW-50: the LivingWeapon adapter over the portable <see cref="FingerprintGuard"/> core. Builds
/// the three DATA-ONLY landmarks the mod checks before any write arms (owner decision: no
/// .text code hash, see docs/TODO.md LW-50 and docs/RELEASE_SCOPE.md section 7):
///   1. PE build key (TimeDateStamp + SizeOfImage of the mapped game image).
///   2. The JobCommand table's rec 8 / rec 9 ability-byte signature (Barrage's own anchor).
///   3. Ramza's roster row shape at RosterBase slot 0.
///
/// BOOT-WINDOW SAFETY: the JobCommand landmark first checks that Ramza's roster row is populated
/// (a save is loaded) before touching the signature windows at all; before a save loads, the
/// table region reads Unreadable (never Mismatch), so the guard never stands down at the title
/// screen on the JobCommand check alone. The PE-key landmark stays independent and is
/// early-decidable: a truly patched exe stands down loudly even at the title screen.
///
/// Once armed, this instance never re-verifies (verify-once is load-bearing: Barrage legitimately
/// edits the same JobCommand region after arming, e.g. when it injects Barrage into a job's
/// command list). LW-53: the armed edge and the stand-down verdict record into the flight ring,
/// and stand-down requests its own "standdown" flush, so a stand-down leaves a non-empty archive.
///
/// LW-83: this file holds the LIFECYCLE half (construction, Step, the hook-arm handshake, and the
/// armed/stand-down edges). The landmark expected-value constants, the two composite Probe*
/// methods, and their observed-vs-expected detail formatting live in the
/// LaunchGuard.Landmarks.cs partial (a real WHAT-vs-lifecycle seam, kept in the same class so the
/// ctor can still reference those constants and probes by plain name).
/// </summary>
internal sealed partial class LaunchGuard
{
    private readonly IGameMemory _mem;
    private readonly FingerprintGuard _guard;
    private readonly GuardLandmark _jobCommandRec8;
    private readonly GuardLandmark _jobCommandRec9;
    private readonly object _hookLock = new();
    private Action? _pendingHookArm;
    private readonly Action<string, string>? _notice;
    // LW-53: flight recorder taps (mirrors TurnTracker/KillTracker/Reliquary/Puppeteer); null default keeps tests green.
    private readonly Action<string, string>? _recorder;
    private readonly Action<string>? _requestFlush;
    // LW-83: the drill ctor arg, stashed so StandDown can self-identify a drill-forced stand-down
    // (Mod.cs compiles this to a hard false outside LWDEV, so a player build never sets it).
    private readonly bool _forceMismatch;

    /// <param name="notice">The OS-level stand-down notice; null (the default) means NO OS notice
    /// fires, which is what every unit test gets so a real message box never pops during
    /// `dotnet test`. Production wires the real notice explicitly from Engine's construction site
    /// rather than defaulting to it here.</param>
    /// <param name="recorder">LW-53: the flight ring tap (production: Flight.Record).</param>
    /// <param name="requestFlush">LW-53: the flight flush request (production: Flight.RequestFlush);
    /// only stand-down calls this, with a dedicated "standdown" trigger.</param>
    public LaunchGuard(IGameMemory mem, bool forceMismatch, Action<string, string>? notice = null,
        Action<string, string>? recorder = null, Action<string>? requestFlush = null)
    {
        _mem = mem;
        _forceMismatch = forceMismatch;
        _notice = notice;
        _recorder = recorder;
        _requestFlush = requestFlush;
        GuardTryRead tryRead = (long a, int n, out byte[] b) => _mem.TryReadBytes(a, n, out b);

        // Always-compiled dev knob (review blocker 2): perturbs the EXPECTED TimeDateStamp so a
        // dev can prove the loud stand-down path fires without needing a real game patch. The
        // Mod.StartEngine call site is the only #if LWDEV point; here it stays a plain bool so
        // Release still compiles this same code path and exercises it with a hard false.
        uint expectedTimeDateStamp = forceMismatch ? ExpectedTimeDateStamp ^ 1 : ExpectedTimeDateStamp;

        _jobCommandRec8 = FingerprintGuard.ByteSignature(tryRead, Rec8Addr, Rec8Sig, "jobcommand-rec8");
        _jobCommandRec9 = FingerprintGuard.ByteSignature(tryRead, Rec9Addr, Rec9Sig, "jobcommand-rec9");

        var landmarks = new[]
        {
            FingerprintGuard.PeBuildKey(tryRead, ModuleBase, expectedTimeDateStamp, ExpectedSizeOfImage),
            FingerprintGuard.Custom("jobcommand-table", ProbeJobCommandTable),
            FingerprintGuard.Custom("ramza-roster-row", ProbeRamzaRosterRow),
        };
        _guard = new FingerprintGuard(landmarks, StandDown, ArmedEdge, MismatchDebounce);
    }

    public bool Armed => _guard.Armed;

    /// <summary>Test/diagnostic seam: the underlying state machine's terminal state, so a test can
    /// tell "still retrying" apart from "permanently stood down" (both read Armed == false).</summary>
    internal GuardState State => _guard.State;

    public void Step() => _guard.Step();

    /// <summary>Arms a deferred hook now if the guard is already Armed, else stashes it for the
    /// Armed edge (exactly once either path; StoodDown never arms). Lets Engine.InjectHooks call
    /// this without duplicating the arm-or-stash choice at every call site.</summary>
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
        // LW-53: no flush request here; the record rides to the next battle-edge flush.
        _recorder?.Invoke("guard", "armed (all landmarks match; writes enabled)");
        ModLogger.Event(LogVerb.Startup,
            "The game build matches all memory landmarks; Living Weapons is armed (writes were held until now).");
        lock (_hookLock)
        {
            var arm = _pendingHookArm;
            _pendingHookArm = null;
            arm?.Invoke();
        }
    }

    private void StandDown(string diag)
    {
        // Mem.WritesEnabled stays false: the absence of an assignment here IS the point (the
        // default state Mod.StartEngine set before construction). LW-53: record first, so a
        // later flush archives WHY the mod stood down.
        // LW-83: a drill-forced stand-down (LW_FORCE_FINGERPRINT_MISMATCH, dev builds only; Mod.cs
        // compiles forceMismatch to a hard false outside LWDEV, so a player build can never reach
        // this branch) self-identifies in the payload and the log line, so an archive is never
        // mistaken for a real game-patch mismatch.
        string payload = _forceMismatch
            ? $"stand-down (drill: LW_FORCE_FINGERPRINT_MISMATCH) ({diag})"
            : $"stand-down ({diag})";
        _recorder?.Invoke("guard", payload);
        string drillNote = _forceMismatch
            ? " This stand-down was forced by the LW_FORCE_FINGERPRINT_MISMATCH drill flag (dev builds only)."
            : "";
        ModLogger.Error(LogVerb.Startup,
            $"The game build does not match this mod's memory landmarks ({diag}); Living Weapons is standing down to protect your save. The mod likely needs an update for a new game patch, or another installed mod has modified the job command tables.{drillNote}");
        // One-shot alongside the log line above: FingerprintGuard.Step only reaches StandDown once
        // (StoodDown is terminal, GuardState never revisits Verifying), so this call fires exactly
        // once per session. A player who never opens the log still learns the mod went inert.
        // Owner-authored copy (2026-07-14 drill review): headline first, softened cause (a
        // mismatch is almost always a game update but can be another mod), switched-off framed
        // as until-resolved, and the email ask carries the logs request inline.
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
        // LW-53: requested LAST and named "standdown", not "error": an earlier unrelated error may
        // already have burnt the "error" FlushOnce latch, and battle edges never fire pre-arm, so
        // riding "error" could strand this record; a distinct trigger avoids that (last writer
        // wins on the pending trigger name, FlightRecorder.cs).
        _requestFlush?.Invoke("standdown");
    }
}
