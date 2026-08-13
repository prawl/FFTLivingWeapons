using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// LW-83: the two-lane trigger for the LW-50 stand-down drill. Lane one is the environment
/// variable (kept for boxes where variable inheritance into the game process works); lane two is
/// a marker FILE named after the flag, living in the mod dir next to the DLL, because env
/// variables do not reach fft_enhanced through this box's launch chain (owner-verified
/// 2026-07-14). BuildLinked's clean step deletes the marker on every deploy, so it cannot linger
/// across builds. ALWAYS COMPILED, mirroring the LaunchGuard forceMismatch precedent (a plain
/// bool, no #if in this class), so Release builds compile and exercise the same code path; the
/// only conditional is the Mod.cs call site, which compiles to const false outside LWDEV, so
/// players can never trigger the drill through either lane.
/// </summary>
internal static class DrillTrigger
{
    internal const string FlagName = "LW_FORCE_FINGERPRINT_MISMATCH";

    /// <summary>Test seam: injected env and file-existence reads, so tests never touch the real
    /// process environment. The env lane matches exactly "1"; any other value (including "0" and
    /// "true") is not a request.</summary>
    internal static bool DrillRequested(string modDir, Func<string, string?> getEnv, Func<string, bool> fileExists)
        => getEnv(FlagName) == "1" || fileExists(Path.Combine(modDir, FlagName));

    /// <summary>Production convenience overload over the real environment and filesystem.</summary>
    internal static bool DrillRequested(string modDir)
        => DrillRequested(modDir, Environment.GetEnvironmentVariable, File.Exists);
}
