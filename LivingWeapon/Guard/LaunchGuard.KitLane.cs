using System;

namespace LivingWeapon;

/// <summary>
/// The kit-lane guard, split out of LaunchGuard.cs/LaunchGuard.Landmarks.cs (200-line trigger, as in
/// the landmarks split). A custom job/ability mod that rewrites the JobCommand rec8/rec9 bytes used to trip the main
/// guard's game-update stand-down. This second FingerprintGuard instance isolates that landmark, so a conflict
/// only disables the three weapon-granted commands it anchors (Barrage, Shadow Blade, Provoke). It steps from the first tick.
/// </summary>
internal sealed partial class LaunchGuard
{
    /// <summary>Steps the kit-lane guard while it is still Verifying. The build stamp is only a report,
    /// so a lane stand-down on an unknown build may be the game update itself, which is why the lane copy hedges then.
    /// Engine.cs calls this once per tick beside the main guard's Step.</summary>
    public void StepKitLane()
    {
        if (_kitLaneGuard.State != GuardState.Verifying) return;
        _kitLaneGuard.Step();
    }

    private LandmarkReading ProbeKitLaneTable()
    {
        // No boot-window gate, unlike ProbeRamzaRosterRow. The rec8/rec9 pair is file baked image data.
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
    /// all-zero-is-Unreadable boot-window rule. The file-baked table (see Rec8Sig/Rec9Sig in
    /// LaunchGuard.Landmarks.cs) can only be zero if another mod blanked it, and Unreadable
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
            "The job command data matches this mod's own copy; the weapon commands (Barrage, Shadow Blade and Provoke) have passed their check and run once the party data check passes too.");
    }

    private void KitLaneStandDown(string diag)
    {
        // Same order as the main StandDown (LaunchGuard.cs). Record, log, flush request.
        _recorder?.Invoke("guard", $"kit-lane stand-down ({diag})");
        // Warn, not Error, since the mod stays up. The text must avoid "standing down to protect your save",
        // which tools/scan_logs.py classifies as a full stand-down.
        ModLogger.Warn(LogVerb.Startup, PartsCopy.Disabled(
            "The weapon commands (Barrage, Shadow Blade and Provoke) are paused for this session because another mod rewrote the job command data this mod needs, and the two mods would otherwise overwrite each other; the rest of Living Weapons keeps working.",
            HedgeIfUnknownBuild(), "", diag + BuildDetail));
        // Reuses the "standdown" trigger; the "kit-lane" marker in the record payload tells the
        // two shapes apart on replay.
        _requestFlush?.Invoke("standdown");
    }
}
