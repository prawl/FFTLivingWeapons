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
/// </summary>
internal sealed class LaunchGuard
{
    // PE fingerprint of the 1.5 exe (docs/research/PORT_1.5_OFFSETS.md:11-15; exe SHA256
    // 3625FD9B...). Pre-1.5 values differ in BOTH fields (0x690C1269 / 0x156C8000), so either
    // field alone would catch a rollback; PE FileVersion is NOT usable (reads 1.0.0.0 on both
    // builds, docs/research/PORT_1.5.md:96-98).
    internal const uint ExpectedTimeDateStamp = 0x6A0F86A9;
    internal const uint ExpectedSizeOfImage = 0x190EB000;
    private const long ModuleBase = 0x140000000L;

    // JobCommand table, rec 8 (Aim, Archer) + rec 9 (Martial Arts, Monk) ability-id bytes. Window
    // addresses = Barrage.AbilityBase (0x14067E213, Barrage.cs:43) + rec * Barrage.RecSize (25,
    // Barrage.cs:44). The unique-hit re-find signature that located AbilityBase after the 1.5
    // recompile is tools/probes/jobcommand_find_probe.py:44-45 (REC8_SIG/REC9_SIG). Rec 14 (Steal)
    // is deliberately dropped: its exact bytes rest on one dump note, weaker provenance than the
    // probe-verified pair above.
    private static readonly long Rec8Addr = Barrage.AbilityBase + 8L * Barrage.RecSize;
    private static readonly long Rec9Addr = Barrage.AbilityBase + 9L * Barrage.RecSize;
    // Aim ability ids 150..157 (0x96..0x9D), Martial Arts ability ids 100..107 (0x64..0x6B): the
    // same bytes as jobcommand_find_probe.py's REC8_SIG/REC9_SIG.
    private static readonly byte[] Rec8Sig = { 0x96, 0x97, 0x98, 0x99, 0x9A, 0x9B, 0x9C, 0x9D };
    private static readonly byte[] Rec9Sig = { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B };

    // Ramza roster row shape at RosterBase slot 0 (Offsets.cs:94). Provenance: Offsets.cs:94/120,
    // unitid_probe captures (Offsets.cs's ANameId doc), docs/research/SPRITE_SWAP.md:41-52 (Ramza
    // sprite 0x01-0x06 across chapters; story bodies stay under 0x80, monsters are >= 0x80, in
    // every chapter).
    private const int RamzaExpectedNameId = 1;
    private const byte MonsterSpriteFloor = 0x80;
    private const byte MaxPlausibleBraveFaith = 100;

    // At the 33ms Engine tick (Engine.cs:19, PollMs), 30 consecutive mismatching Steps is about
    // 30 * 33ms = 990ms: roughly one second before a permanent stand-down.
    private const int MismatchDebounce = 30;

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
        _recorder?.Invoke("guard", $"stand-down ({diag})");
        ModLogger.Error(LogVerb.Startup,
            $"The game build does not match this mod's memory landmarks ({diag}); Living Weapons is standing down to protect your save. The mod likely needs an update for a new game patch, or another installed mod has modified the job command tables.");
        // One-shot alongside the log line above: FingerprintGuard.Step only reaches StandDown once
        // (StoodDown is terminal, GuardState never revisits Verifying), so this call fires exactly
        // once per session. A player who never opens the log still learns the mod went inert.
        _notice?.Invoke("FFT Living Weapons",
            "The Living Weapons mod checked your game and it does not look like the version "
            + "the mod was built for. The game probably got an update.\n\n"
            + "To keep your save file safe, the mod has switched itself off for now. Your game "
            + "will still run normally, just without Living Weapons.\n\n"
            + "How to fix it: check for a newer version of the mod.\n\n"
            + "Still stuck? Email me at ptyrawl@gmail.com and I will help. If you do, please copy "
            + "and paste your logs into a text file and send it with your email.\n\n"
            + "You can get the logs two ways: copy all the text from the black command prompt "
            + "window that appears when you launch the game, or click the Open Folder button on "
            + "this mod's page in Reloaded-II and open the file named livingweapon.log.");
        // LW-53: requested LAST and named "standdown", not "error": an earlier unrelated error may
        // already have burnt the "error" FlushOnce latch, and battle edges never fire pre-arm, so
        // riding "error" could strand this record; a distinct trigger avoids that (last writer
        // wins on the pending trigger name, FlightRecorder.cs).
        _requestFlush?.Invoke("standdown");
    }

    private LandmarkVerdict ProbeJobCommandTable()
    {
        // BOOT-WINDOW SAFETY: a loaded save implies the boot-built JobCommand table (learn
        // screens work), so gate on Ramza's roster row being populated at all before reading the
        // signature windows. Game knowledge stays here in the adapter; FingerprintGuard's core
        // stays agnostic of it.
        int level = _mem.U8(Offsets.RosterBase + Offsets.RLevel);
        if (level < 1 || level > 99) return LandmarkVerdict.Unreadable;

        var rec8 = _jobCommandRec8.Probe();
        var rec9 = _jobCommandRec9.Probe();
        if (rec8 == LandmarkVerdict.Match && rec9 == LandmarkVerdict.Match) return LandmarkVerdict.Match;
        if (rec8 == LandmarkVerdict.Mismatch || rec9 == LandmarkVerdict.Mismatch) return LandmarkVerdict.Mismatch;
        return LandmarkVerdict.Unreadable;
    }

    private LandmarkVerdict ProbeRamzaRosterRow()
    {
        long rb = Offsets.RosterBase;
        int level = _mem.U8(rb + Offsets.RLevel);
        if (level < 1 || level > 99) return LandmarkVerdict.Unreadable;   // unpopulated slot (title screen)

        int nameId = _mem.U16(rb + Offsets.RNameId);
        byte sprite = _mem.U8(rb + Offsets.RSprite);
        int brave = _mem.U8(rb + Offsets.RBrave);
        int faith = _mem.U8(rb + Offsets.RFaith);

        return nameId == RamzaExpectedNameId && sprite < MonsterSpriteFloor
               && brave <= MaxPlausibleBraveFaith && faith <= MaxPlausibleBraveFaith
            ? LandmarkVerdict.Match : LandmarkVerdict.Mismatch;
    }
}
