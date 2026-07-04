using System;

namespace LivingWeapon;

/// <summary>
/// Logging contract for the runtime -- ported from FFTColorCustomizer's ModLogger/ILogger split
/// (ColorMod/Interfaces/ILogger.cs, ColorMod/Utilities/ModLogger.cs) so both mods share one
/// logging shape. <see cref="ModLogger"/> is the static facade every call site uses;
/// <see cref="FileConsoleLogger"/> is the production implementation and <see cref="NullLogger"/>
/// the test-only swallow-everything one. LogLevel is the programmatic "turn a whole tier on/off"
/// control; Config.VerboseLog (Configuration/Config.cs) is the simple user-facing knob that maps
/// onto it once at startup (see Mod.cs).
/// </summary>
internal interface ILogger
{
    /// <summary>Console volume threshold -- see <see cref="FileConsoleLogger"/> for the two-sink
    /// semantics (the file evidence chain ignores this entirely).</summary>
    LogLevel LogLevel { get; set; }

    /// <summary>Standard informational message (the old Log.Info).</summary>
    void Log(string message);

    /// <summary>Error message (the old Log.Error).</summary>
    void LogError(string message);

    /// <summary>Error message with exception detail appended.</summary>
    void LogError(string message, Exception exception);

    /// <summary>Warning message -- unused by any call site yet (no LivingWeapon module has
    /// needed the tier), but part of the ported interface shape.</summary>
    void LogWarning(string message);

    /// <summary>Debug-tier message: file-always, console only when the threshold allows it.</summary>
    void LogDebug(string message);
}

/// <summary>Verbosity tiers, low (most verbose) to high. A configured <see cref="LogLevel"/> of N
/// allows console output for any call at tier &gt;= N; None (4) silences the console entirely.</summary>
internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4,
}
