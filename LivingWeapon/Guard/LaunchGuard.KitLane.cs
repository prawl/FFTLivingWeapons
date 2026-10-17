using System;

namespace LivingWeapon;

/// <summary>
/// The kit-lane guard, split out of LaunchGuard.cs/LaunchGuard.Landmarks.cs (200-line trigger, as in
/// the landmarks split). A custom job/ability mod that rewrites the JobCommand rec8/rec9 bytes used to trip the main
/// guard's game-update stand-down. This second FingerprintGuard instance isolates that landmark, so a conflict
/// only disables the three weapon-granted commands it anchors (Barrage, Shadow Blade, Provoke).
/// </summary>
internal sealed partial class LaunchGuard
{
    /// <summary>steps the kit-lane guard only while the main guard is Armed and the lane is
    /// still Verifying. The build stamp is only a report, so a lane stand-down on an
    /// unknown build may be the game update itself, which is why the lane copy hedges then.
    /// Engine.cs calls this once per tick beside the main guard's Step().</summary>
    public void StepKitLane()
    {
        if (_guard.State != GuardState.Armed) return;
        if (_kitLaneGuard.State != GuardState.Verifying) return;
        _kitLaneGuard.Step();
    }

    private LandmarkReading ProbeKitLaneTable()
    {
        // No boot-window gate (unlike ProbeRamzaRosterRow, LaunchGuard.Landmarks.cs): StepKitLane only
        // steps after the main guard armed, which needs a populated Ramza row.
        bool ok8 = _mem.TryReadBytes(Rec8Addr, Rec8Sig.Length, out var buf8) && buf8.Length >= Rec8Sig.Length;
        bool ok9 = _mem.TryReadBytes(Rec9Addr, Rec9Sig.Length, out var buf9) && buf9.Length >= Rec9Sig.Length;
        if (!ok8 || !ok9) return LandmarkVerdict.Unreadable;   // a failed/short read is transient. Streak resets, benign

        string? d8 = DescribeKitLaneMismatch(buf8, Rec8Sig, "rec8");
        string? d9 = DescribeKitLaneMismatch(buf9, Rec9Sig, "rec9");
        if (d8 is null && d9 is null) return LandmarkVerdict.Match;
        // Only the mismatching rec(s) contribute detail.
        string detail = d8 is not null && d9 is not null ? $"{d8}; {d9}" : (d8 ?? d9)!;
        return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
    }

    /// <summary>Null on a match. An all-zero window is a Mismatch here, unlike FingerprintGuard.ByteSignature's
    /// all-zero-is-Unreadable boot-window rule. This probe runs post-arm, when the file-baked table (see
    /// Rec8Sig/Rec9Sig in LaunchGuard.Landmarks.cs) can only be zero if another mod blanked it, and Unreadable
    /// would retry forever with no message (the bug the kit lane fixed).</summary>
    private static string? DescribeKitLaneMismatch(byte[] observed, byte[] expected, string recName)
    {
        bool allZero = true;
        for (int i = 0; i < expected.Length; i++)
            if (observed[i] != 0) { allZero = false; break; }
        if (allZero) return $"{recName} reads all zero (blanked)";

        for (int i = 0; i < expected.Length; i++)
            if (observed[i] != expected[i])
                return $"{recName}: expected {BitConverter.ToString(expected)}, "
                    + $"observed {BitConverter.ToString(observed, 0, expected.Length)}";
        return null;
    }

    private void KitLaneArmedEdge()
    {
        _recorder?.Invoke("guard", "kit lane armed (JobCommand table matches; weapon-granted commands enabled)");
        ModLogger.Event(LogVerb.Startup,
            "The JobCommand table matches this mod's own anchor; the weapon-granted commands (Barrage, Shadow Blade, Provoke) are enabled.");
    }

    private void KitLaneStandDown(string diag)
    {
        // Same order as the main StandDown (LaunchGuard.cs): record, log, notice, flush request.
        _recorder?.Invoke("guard", $"kit-lane stand-down ({diag})");
        // Warn, not Error. The mod stays up. The text must avoid "standing down to protect your save",
        // which tools/scan_logs.py:59 classifies as a full stand-down.
        ModLogger.Warn(LogVerb.Startup,
            $"Another mod has rewritten the job command data this mod anchors its weapon-granted commands to ({diag}). "
            + "Living Weapons stays armed and everything else keeps working; the Barrage, Shadow Blade and Provoke "
            + "command grants are disabled for this session so the two mods do not overwrite each other."
            + HedgeIfUnknownBuild());   // on a new game build the update itself may be the cause
        // Calm copy. Only three optional command grants stepped back.
        _notice?.Invoke("FFT Living Weapons",
            "Living Weapons found another mod editing the same game data\n\n"
            + "Another installed mod (usually a custom job or ability mod) changed the game's job "
            + "command data, which Living Weapons also uses for three weapon-granted commands.\n\n"
            + "Living Weapons is still running normally: kill counts, weapon growth, and every other "
            + "power all work.\n\n"
            + "Only the three weapon-granted commands (Barrage, Shadow Blade, Provoke) are switched "
            + "off this session, so the two mods do not overwrite each other. No action needed.\n\n"
            + "If you want those commands back, the only fix is removing the conflicting mod; load "
            + "order does not change which mod wins." + HedgeIfUnknownBuild() + "\n\n"
            + "Questions? Email me at ptyrawl@gmail.com.");
        // Reuses the "standdown" trigger; the "kit-lane" marker in the record payload tells the
        // two shapes apart on replay.
        _requestFlush?.Invoke("standdown");
    }
}
