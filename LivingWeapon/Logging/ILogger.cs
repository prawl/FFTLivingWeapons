using System;

namespace LivingWeapon;

/// <summary>
/// Logging interface, ported from FFTColorCustomizer's ILogger/ModLogger split (ColorMod/Interfaces/ILogger.cs,
/// ColorMod/Utilities/ModLogger.cs). <see cref="ModLogger"/> is the static facade, <see cref="FileConsoleLogger"/>
/// the production implementation, <see cref="NullLogger"/> the test one. Mod.cs pins LogLevel to Info at startup
/// (the VerboseLog knob is gone); raise it to Debug there for live diagnosis.
/// </summary>
internal interface ILogger
{
    /// <summary>Console volume threshold; the file sink ignores it (see <see cref="FileConsoleLogger"/>).</summary>
    LogLevel LogLevel { get; set; }

    // Verb-aware entry points. The file line always carries "[verb] "; the console carries it only at Warning/Error,
    // and Info lines render subject-first (see FileConsoleLogger). The verb-less members are deleted;
    // LogContractTests trips if one returns.

    /// <summary>Info tier. File "[LEVEL] [verb] message", console "[LEVEL] message".</summary>
    void Log(LogVerb verb, string message);

    /// <summary>Warning tier. Both sinks carry "[verb] " so console pastes can be triaged.</summary>
    void LogWarning(LogVerb verb, string message);

    /// <summary>Error-tier, verb-aware. Both sinks carry "[verb] ".</summary>
    void LogError(LogVerb verb, string message);

    /// <summary>Error-tier, verb-aware, with exception detail appended. Both sinks carry "[verb] ".</summary>
    void LogError(LogVerb verb, string message, Exception exception);

    /// <summary>Debug tier. The file always carries "[verb] "; a raised console carries it too.</summary>
    void LogDebug(LogVerb verb, string message);

    /// <summary>Resets the console-only per-battle dedup set (logging facelift conflict C1); the file keeps every line.
    /// Called from Engine on battle enter and exit via <see cref="ModLogger.NoteBattleEdge"/>.</summary>
    void NoteBattleEdge();
}

/// <summary>Verbosity tiers, most verbose first. Console output is allowed for calls at tier &gt;= LogLevel; None (4) silences it.</summary>
internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4,
}
