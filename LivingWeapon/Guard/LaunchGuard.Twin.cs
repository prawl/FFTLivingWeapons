using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The twin weapon guard. It checks the two menu flags the twin grant reads to stay out of the equipment screens
/// and the hand words of Ramza's roster row, once a save has loaded. It steps from the first tick and is terminal.
/// </summary>
internal sealed partial class LaunchGuard
{
    internal const string TwinFlagsUnreadable = "menu flags unreadable";
    // Item ids end at 511 and every roster ability key sits under 512.
    private const int MaxPlausibleRosterWord = 0x1FF;
    private const int EmptyRosterWord = 0xFFFF;
    private const string TwinDrillDetail = "drill: " + DrillTrigger.TwinFlagName;

    private readonly FingerprintGuard _twinGuard;
    private readonly bool _forceTwinMismatch;
    private string? _twinDetail;
    private bool _twinStallNoted;

    /// <summary>Test seam. The twin guard's terminal state.</summary>
    internal GuardState TwinState => _twinGuard.State;

    /// <summary>Steps the twin guard while it is still Verifying. Engine.cs calls this once per tick.</summary>
    public void StepTwin()
    {
        if (_twinGuard.State != GuardState.Verifying) return;
        _twinGuard.Step();
    }

    private LandmarkReading ProbeTwinFlags()
    {
        long rb = Offsets.RosterBase;
        int level = _mem.U8(rb + Offsets.RLevel);
        if (level < 1 || level > 99)
            return new LandmarkReading(LandmarkVerdict.Unreadable,
                _mem.TryReadBytes(rb + Offsets.RLevel, 1, out _) ? RosterSlotEmpty : RosterPageUnreadable);

        if (!_mem.TryReadBytes(Offsets.MenuOpenFlag, 1, out var menu) || menu.Length < 1
            || !_mem.TryReadBytes(Offsets.PartyBrowseFlag, 1, out var browse) || browse.Length < 1)
            return new LandmarkReading(LandmarkVerdict.Unreadable, TwinFlagsUnreadable);

        var faults = new List<string>();
        if (menu[0] > 1 || browse[0] > 1)
            faults.Add($"menu flag reads {menu[0]}, browse flag reads {browse[0]} (expected 0 or 1 each)");
        AddHandFault(faults, "right hand", rb + Offsets.RRHand);
        AddHandFault(faults, "off hand", rb + Offsets.ROffHand);
        AddHandFault(faults, "shield", rb + Offsets.RShield);
        AddHandFault(faults, "support", rb + Offsets.RSupport);
        return faults.Count == 0
            ? LandmarkVerdict.Match
            : new LandmarkReading(LandmarkVerdict.Mismatch, string.Join("; ", faults));
    }

    private void AddHandFault(List<string> faults, string name, long addr)
    {
        int word = _mem.U16(addr);
        if (word > MaxPlausibleRosterWord && word != EmptyRosterWord) faults.Add($"{name} reads 0x{word:X4}");
    }

    // Only the twin's own unreadable flags feed the stall timer. An empty slot or an unreadable roster page
    // belongs to the roster door.
    private GuardLandmark TwinLandmark() => LandmarkStall.Watch(
        TwinTap(TwinDrill(FingerprintGuard.Custom("twin-menu-flags", ProbeTwinFlags))),
        DoorWaitSteps, OnTwinStalled, r => r.Detail == TwinFlagsUnreadable);

    private GuardLandmark TwinTap(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        _twinDetail = reading.Verdict == LandmarkVerdict.Unreadable ? reading.Detail : null;
        return reading;
    });

    private GuardLandmark TwinDrill(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        if (!_forceTwinMismatch || reading.Verdict == LandmarkVerdict.Unreadable) return reading;
        if (reading.Verdict == LandmarkVerdict.Mismatch)
            return new LandmarkReading(LandmarkVerdict.Mismatch, reading.Detail + "; " + TwinDrillDetail);
        return new LandmarkReading(LandmarkVerdict.Mismatch, TwinDrillDetail);
    });

    private void OnTwinStalled(string name, string? detail)
        => NoteDoorStall(ref _twinStallNoted, "menu flag", "twin weapons stays");

    private void TwinArmedEdge()
    {
        _recorder?.Invoke("guard", "twin lane armed (menu flags match; twin weapon grant enabled)");
        ModLogger.Event(LogVerb.Startup, "The menu flags read as expected; the twin weapon grant is enabled.");
    }

    private void TwinStandDown(string diag)
    {
        _recorder?.Invoke("guard", $"twin lane stand-down ({diag})");
        string drillNote = _forceTwinMismatch ? " " + PartsReport.TwinDrillSentence : "";
        // Warn, not Error, and the copy avoids the scanner's full stand down phrase.
        ModLogger.Warn(LogVerb.Startup, PartsCopy.Disabled(
            "The twin weapon grant is paused for this session because the two menu flags it uses to stay out of the equipment screens do not read the way this mod expects; kill counting and everything else keep working.",
            HedgeIfUnknownBuild(), drillNote, diag + BuildDetail));
        _requestFlush?.Invoke("standdown");
    }
}
