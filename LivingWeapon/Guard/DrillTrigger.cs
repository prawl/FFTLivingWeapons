using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Two-lane trigger for the stand-down drill. Lane one is the environment variable; lane two is a marker file named after the flag in the mod dir,
/// because env variables do not reach fft_enhanced through this box's launch chain (2026-07-14). BuildLinked's clean step deletes the marker on every deploy.
/// Always compiled, like the LaunchGuard forceMismatch; only the Mod.cs call site is conditional (const false outside LWDEV), so players cannot trigger the drill.
/// </summary>
internal static class DrillTrigger
{
    internal const string FlagName = "LW_FORCE_FINGERPRINT_MISMATCH";

    /// <summary>the sensing lane's drill flag; forces its mismatch on healthy memory.</summary>
    internal const string SensingFlagName = "LW_FORCE_SENSING_MISMATCH";

    /// <summary>the blind-guard drill flag; makes the main guard's landmarks read Unreadable for a while so the stall warning and recovery line can be watched.</summary>
    internal const string UnreadableFlagName = "LW_FORCE_GUARD_UNREADABLE";

    /// <summary>the roster landmark's drill flag; forces a populated healthy row to read as a mismatch so the parts report can be watched.</summary>
    internal const string RosterFlagName = "LW_FORCE_ROSTER_MISMATCH";

    /// <summary>Any flag name through injected env and file-existence reads, so tests never touch the real environment.
    /// The env lane matches only "1"; a flag's marker file never satisfies a different flag.</summary>
    internal static bool DrillRequested(string modDir, string flagName, Func<string, string?> getEnv, Func<string, bool> fileExists)
        => getEnv(flagName) == "1" || fileExists(Path.Combine(modDir, flagName));

    /// <summary>Test seam for the fingerprint flag.</summary>
    internal static bool DrillRequested(string modDir, Func<string, string?> getEnv, Func<string, bool> fileExists)
        => DrillRequested(modDir, FlagName, getEnv, fileExists);

    /// <summary>Production overload over the real environment and filesystem.</summary>
    internal static bool DrillRequested(string modDir)
        => DrillRequested(modDir, FlagName);

    /// <summary>Production overload for any flag over the real environment and filesystem.</summary>
    internal static bool DrillRequested(string modDir, string flagName)
        => DrillRequested(modDir, flagName, Environment.GetEnvironmentVariable, File.Exists);
}
