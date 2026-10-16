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

    /// <summary>LW-395: the sensing lane's drill flag, same two lanes (env "1" or a marker file
    /// of this name in the mod dir). Forces the sensing lane's mismatch on healthy memory.</summary>
    internal const string SensingFlagName = "LW_FORCE_SENSING_MISMATCH";

    /// <summary>General form: any flag name through the injected env and file-existence reads, so
    /// tests never touch the real process environment. The env lane matches exactly "1"; any other
    /// value (including "0" and "true") is not a request. A flag's marker file never satisfies a
    /// different flag.</summary>
    internal static bool DrillRequested(string modDir, string flagName, Func<string, string?> getEnv, Func<string, bool> fileExists)
        => getEnv(flagName) == "1" || fileExists(Path.Combine(modDir, flagName));

    /// <summary>Test seam for the original (fingerprint) flag.</summary>
    internal static bool DrillRequested(string modDir, Func<string, string?> getEnv, Func<string, bool> fileExists)
        => DrillRequested(modDir, FlagName, getEnv, fileExists);

    /// <summary>Production convenience overload over the real environment and filesystem.</summary>
    internal static bool DrillRequested(string modDir)
        => DrillRequested(modDir, FlagName);

    /// <summary>Production overload for any flag over the real environment and filesystem.</summary>
    internal static bool DrillRequested(string modDir, string flagName)
        => DrillRequested(modDir, flagName, Environment.GetEnvironmentVariable, File.Exists);
}
