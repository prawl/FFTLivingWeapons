namespace LivingWeapon;

/// <summary>
/// LW-397 seam: the BUILD STAMP half of the main guard. The game's build stamp (the PE header's
/// TimeDateStamp and SizeOfImage) used to be a stand-down landmark: any stamp the mod was not
/// built against switched the WHOLE mod off. In 2026-09 the game was recompiled with nothing
/// moved (docs/CHANGELOG.md, LW-387 row), the stamp changed, and the mod went dark over nothing.
/// By owner ruling (docs/TODO.md, LW-397 row) the stamp is now REPORTED and never used as a
/// switch: one startup log line says what the game is, and when the mod arms on a stamp it does
/// not know it adds one calm warning. The only main-guard landmark that can stand the mod down is
/// Ramza's roster row (LaunchGuard.Landmarks.cs).
///
/// The first landmark keeps its name and slot in the main guard so the LW-392 stall decorator
/// still watches an unreadable game exactly as before. <see cref="ProbeBuildStamp"/> returns
/// Unreadable on a failed read and Match on EVERY readable one, known stamp or not.
///
/// THREADING: the latch fields are written and read on the Engine tick thread only (Step runs
/// there and the armed edge runs inside Step), so they carry no volatile or lock. When a later
/// row reads them from another thread (LW-393's per-part write gates), that row must add the
/// publication it needs.
///
/// LATCH: the first readable read decides known-or-not for the whole process (one LaunchGuard per
/// process, the same permanence argument as the stall latches in LaunchGuard.Stall.cs).
///
/// INVARIANT: the armed edge can only run after the stamp landmark returned Match, which needs a
/// readable read, so <c>_stampRead</c> is always true by then. <see cref="LogUnknownBuildIfAny"/>
/// still early-returns on a false latch rather than assume it.
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

    // The EXPECTED TimeDateStamp the probe compares against (drill-perturbed by the ctor), and the
    // image base the header is read from (captured at construction, as the old landmark did).
    private readonly uint _expectedStampTds;
    private readonly long _stampImageBase;
    // Latched by the first readable read (see the class doc).
    private uint _stampTds;
    private uint _stampSoi;
    private bool _stampRead;
    private bool _stampKnown;

    /// <summary>Test/diagnostic seam: null until the header was read, then whether it equals the
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

    /// <summary>The main guard's first landmark: reads the header, latches and reports it once, and
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
        // Record only when something is off (the LW-392 precedent): a known stamp adds no record.
        _recorder?.Invoke("guard", $"build stamp unknown ({StampText(tds, soi)}; expected 0x{ExpectedTimeDateStamp:X8} / 0x{ExpectedSizeOfImage:X8})");
        // Under the drill both sides of the comparison print identical numbers, so the line says why.
        ModLogger.Event(LogVerb.Startup, UnknownStampLine(tds, soi) + (_forceMismatch ? DrillStampSentence : ""));
    }

    /// <summary>Called from the armed edge: when the mod armed on a stamp it does not know, say so
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
