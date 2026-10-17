namespace LivingWeapon;

/// <summary>
/// The build-stamp landmark of the main guard. The PE TimeDateStamp and SizeOfImage used to stand the
/// whole mod down on an unknown build. The 2026-09 recompile moved nothing but changed the stamp
/// (docs/CHANGELOG.md, LW-387 row) and the mod went dark, so the stamp is now only reported (docs/TODO.md,
/// LW-397 row) and only Ramza's roster row (LaunchGuard.Landmarks.cs) can stand the mod down.
/// <see cref="ProbeBuildStamp"/> keeps the first landmark's slot for the LW-392 stall decorator and returns
/// Unreadable or Match, never Mismatch. <see cref="LogUnknownBuildIfAny"/> still checks the latch.
/// </summary>
internal sealed partial class LaunchGuard
{
    /// <summary>Appended to the kit-lane and sensing stand-down copy only when the build is unknown:
    /// on a new build the cause may be the game update itself, not another installed mod.</summary>
    internal const string UnknownBuildHedge =
        " This game build is new to the mod, so the cause may be the game update itself rather than another installed mod.";
    /// <summary>Appended to the armed-edge warning when the LW_FORCE_FINGERPRINT_MISMATCH drill forced the unknown stamp.</summary>
    internal const string DrillStampSentence =
        " This warning was forced by the LW_FORCE_FINGERPRINT_MISMATCH drill flag (dev builds only).";

    // Expected TimeDateStamp (drill-perturbed by the ctor) and the image base the header is read from.
    private readonly uint _expectedStampTds;
    private readonly long _stampImageBase;
    // Latched by the first readable read, which decides known-or-not for the process. Written on the
    // Engine tick thread only, so no lock; LW-393's per-part write gates must add publication if they
    // read these from another thread.
    private uint _stampTds;
    private uint _stampSoi;
    private bool _stampRead;
    private bool _stampKnown;

    /// <summary>Test/diagnostic seam. Null until the header was read, then whether it equals the
    /// build this mod was made for.</summary>
    internal bool? BuildStampKnown => _stampRead ? _stampKnown : null;

    internal static string StampText(uint tds, uint soi) => $"TimeDateStamp 0x{tds:X8}, SizeOfImage 0x{soi:X8}";

    private static string MadeForText() => StampText(ExpectedTimeDateStamp, ExpectedSizeOfImage);

    internal static string KnownStampLine(uint tds, uint soi) =>
        $"Game build stamp: {StampText(tds, soi)}; the build this mod was made for.";

    internal static string UnknownStampLine(uint tds, uint soi) =>
        $"Game build stamp: {StampText(tds, soi)}; not a build this mod knows (it was made for {MadeForText()}). "
        + "Its own checks decide what runs; see the armed or warning line that follows once a save loads.";

    internal static string UnknownBuildWarn(uint tds, uint soi) =>
        "This game build is new to the mod, its own checks passed, running normally. "
        + $"({StampText(tds, soi)}; the mod was made for 0x{ExpectedTimeDateStamp:X8} / 0x{ExpectedSizeOfImage:X8}.) "
        + "If kill counts stop moving or anything looks off, check the Living Weapons mod page for a newer version.";

    private string HedgeIfUnknownBuild() => _stampRead && !_stampKnown ? UnknownBuildHedge : "";

    private bool TryReadForStamp(long addr, int len, out byte[] buf) => _mem.TryReadBytes(addr, len, out buf);

    /// <summary>The main guard's first landmark. Reads the header, latches and reports it once, and
    /// never returns Mismatch.</summary>
    private LandmarkReading ProbeBuildStamp()
    {
        if (!FingerprintGuard.TryReadPeBuildKey(TryReadForStamp, _stampImageBase, out uint tds, out uint soi))
            return LandmarkVerdict.Unreadable;
        if (!_stampRead) LatchBuildStamp(tds, soi);
        return LandmarkVerdict.Match;
    }

    private void LatchBuildStamp(uint tds, uint soi)
    {
        _stampTds = tds;
        _stampSoi = soi;
        _stampKnown = tds == _expectedStampTds && soi == ExpectedSizeOfImage;
        _stampRead = true;
        if (_stampKnown)
        {
            ModLogger.Event(LogVerb.Startup, KnownStampLine(tds, soi));
            return;
        }
        // Record only when something is off. A known stamp adds no record.
        _recorder?.Invoke("guard", $"build stamp unknown ({StampText(tds, soi)}; expected 0x{ExpectedTimeDateStamp:X8} / 0x{ExpectedSizeOfImage:X8})");
        // Under the drill both sides print identical numbers, so the line says why.
        ModLogger.Event(LogVerb.Startup, UnknownStampLine(tds, soi) + (_forceMismatch ? DrillStampSentence : ""));
    }

    /// <summary>Called from the armed edge. When the mod armed on a stamp it does not know, say so
    /// once, calmly, with no OS message box.</summary>
    private void LogUnknownBuildIfAny()
    {
        if (!_stampRead || _stampKnown) return;
        string tag = _forceMismatch ? " (drill: LW_FORCE_FINGERPRINT_MISMATCH)" : "";
        _recorder?.Invoke("guard", $"armed on an unknown build{tag} ({StampText(_stampTds, _stampSoi)})");
        ModLogger.Warn(LogVerb.Startup,
            UnknownBuildWarn(_stampTds, _stampSoi) + (_forceMismatch ? DrillStampSentence : ""));
    }
}
