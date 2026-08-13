using System;

namespace LivingWeapon;

/// <summary>
/// LW-112 seam: the kit-lane guard's behavior, split out of LaunchGuard.cs/LaunchGuard.Landmarks.cs
/// under the same 200-line precedent as the LW-83 split. A custom job/ability mod that legitimately
/// rewrites the JobCommand rec8/rec9 bytes used to false-positive the MAIN guard's "the game
/// updated" stand-down (LW-112's bug report); this second, independent FingerprintGuard instance
/// isolates that landmark so a conflict there only ever disables the three weapon-granted commands
/// (Barrage, Shadow Blade, Provoke) it anchors, never the whole mod.
/// </summary>
internal sealed partial class LaunchGuard
{
    /// <summary>LW-112: steps the kit-lane guard, but ONLY while the main guard is Armed and the
    /// lane itself is still Verifying (a cheap no-op every other tick, including every tick before
    /// the main guard arms and every tick after the lane reaches its own terminal state).
    ///
    /// This ordering is LOAD-BEARING, not an optimization: the lane can only ever stand down AFTER
    /// the main guard already proved the game build itself matches every DATA-ONLY landmark, so a
    /// lane stand-down can never be mis-narrated as "the game updated" -- structurally, it can only
    /// mean another installed mod rewrote the JobCommand table the weapon-granted commands share.
    /// Engine.cs calls this once per tick, right beside the main guard's own Step().</summary>
    public void StepKitLane()
    {
        if (_guard.State != GuardState.Armed) return;
        if (_kitLaneGuard.State != GuardState.Verifying) return;
        _kitLaneGuard.Step();
    }

    private LandmarkReading ProbeKitLaneTable()
    {
        // No roster-level boot-window gate here (contrast ProbeRamzaRosterRow's own internal gate,
        // LaunchGuard.Landmarks.cs): StepKitLane above never calls Step() until the main guard is
        // Armed, which itself required a populated Ramza row, so this probe never runs pre-save.
        bool ok8 = _mem.TryReadBytes(Rec8Addr, Rec8Sig.Length, out var buf8) && buf8.Length >= Rec8Sig.Length;
        bool ok9 = _mem.TryReadBytes(Rec9Addr, Rec9Sig.Length, out var buf9) && buf9.Length >= Rec9Sig.Length;
        if (!ok8 || !ok9) return LandmarkVerdict.Unreadable;   // a failed/short read is transient: streak resets, benign

        string? d8 = DescribeKitLaneMismatch(buf8, Rec8Sig, "rec8");
        string? d9 = DescribeKitLaneMismatch(buf9, Rec9Sig, "rec9");
        if (d8 is null && d9 is null) return LandmarkVerdict.Match;
        // LW-83 precedent: only the mismatching rec(s) contribute their inner detail.
        string detail = d8 is not null && d9 is not null ? $"{d8}; {d9}" : (d8 ?? d9)!;
        return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
    }

    /// <summary>Null on a match. WHY an all-zero window is a Mismatch here, diverging from
    /// FingerprintGuard.ByteSignature's own all-zero-is-Unreadable rule (FingerprintGuard.cs): that
    /// core rule is a boot-window escape for a table region the engine has not built yet. This
    /// probe never runs pre-arm (StepKitLane's gate above), and post-arm the JobCommand table is
    /// file-baked, mapped, non-zero image data (LaunchGuard.Landmarks.cs's Rec8Sig/Rec9Sig
    /// provenance comment), so an all-zero window at this point can only mean another mod blanked
    /// the row. Treating that as Unreadable would leave the lane retrying forever with no message
    /// ever surfacing -- the exact silent-absence confusion LW-112 exists to kill -- so it reads as
    /// a Mismatch instead, with its own distinct diagnostic.</summary>
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
        // Same ordering as the main StandDown (LaunchGuard.cs): record FIRST so a later flush
        // archives WHY, then the log line, then the notice, then the flush request LAST.
        _recorder?.Invoke("guard", $"kit-lane stand-down ({diag})");
        // WARNING tier, not Error: the mod is NOT down, so this must never read as a full stand-down
        // (and must never contain the phrase "standing down to protect your save" --
        // tools/scan_logs.py:59 classifies that exact phrase as one, and would misreport a healthy
        // mod as dead).
        ModLogger.Warn(LogVerb.Startup,
            $"Another mod has rewritten the job command data this mod anchors its weapon-granted commands to ({diag}). "
            + "Living Weapons stays armed and everything else keeps working; the Barrage, Shadow Blade and Provoke "
            + "command grants are disabled for this session so the two mods do not overwrite each other.");
        // Calm, owner-voice copy: NOT the game-update notice's headline or "switched itself off"
        // framing -- this mod is fine, only three optional command grants stepped back.
        _notice?.Invoke("FFT Living Weapons",
            "Living Weapons found another mod editing the same game data\n\n"
            + "Another installed mod (usually a custom job or ability mod) changed the game's job "
            + "command data, which Living Weapons also uses for three weapon-granted commands.\n\n"
            + "Living Weapons is still running normally: kill counts, weapon growth, and every other "
            + "power all work.\n\n"
            + "Only the three weapon-granted commands (Barrage, Shadow Blade, Provoke) are switched "
            + "off this session, so the two mods do not overwrite each other. No action needed.\n\n"
            + "If you want those commands back, the only fix is removing the conflicting mod; load "
            + "order does not change which mod wins.\n\n"
            + "Questions? Email me at ptyrawl@gmail.com.");
        // LW-53: reuses the existing "standdown" trigger rather than inventing a new one --
        // flight_*_standdown.jsonl now covers both a full stand-down and a kit-lane stand-down; the
        // record payload's "kit-lane" marker (above) is what tells the two shapes apart on replay.
        _requestFlush?.Invoke("standdown");
    }
}
