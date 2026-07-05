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

    /// <summary>Standard informational message (the old Log.Info). Verb-less: renders with no
    /// "[verb]" segment on either sink (the legacy, mid-migration shape).</summary>
    void Log(string message);

    /// <summary>Error message (the old Log.Error). Verb-less variant.</summary>
    void LogError(string message);

    /// <summary>Error message with exception detail appended. Verb-less variant.</summary>
    void LogError(string message, Exception exception);

    /// <summary>Warning message. Verb-less variant.</summary>
    void LogWarning(string message);

    /// <summary>Debug-tier message: file-always, console only when the threshold allows it.
    /// Verb-less variant.</summary>
    void LogDebug(string message);

    // --- Verb-aware overloads (ModLogger's typed facade: Event/Warn/Error/Debug). The FILE line
    // always carries "[verb] "; the CONSOLE line carries it only at Warning/Error tier. An Info
    // console line (the match report's narrative sentences) renders subject-first with no leading
    // bracket. See FileConsoleLogger's class doc for the exact rendering split. ---

    /// <summary>Info-tier, verb-aware. File: "[LEVEL] [verb] message". Console: "[LEVEL] message"
    /// (verb omitted).</summary>
    void Log(LogVerb verb, string message);

    /// <summary>Warning-tier, verb-aware. Both sinks carry "[verb] " (bug-report console pastes
    /// need the verb for triage).</summary>
    void LogWarning(LogVerb verb, string message);

    /// <summary>Error-tier, verb-aware. Both sinks carry "[verb] ".</summary>
    void LogError(LogVerb verb, string message);

    /// <summary>Error-tier, verb-aware, with exception detail appended. Both sinks carry "[verb] ".</summary>
    void LogError(LogVerb verb, string message, Exception exception);

    /// <summary>Debug-tier, verb-aware. File always carries "[verb] "; when VerboseLog surfaces a
    /// Debug line to the console, the verb rides along too (a diagnostic tier, not the curated
    /// match-report narrative the Info/console split is for).</summary>
    void LogDebug(LogVerb verb, string message);

    /// <summary>Resets the console-only per-battle dedup seen-set (conflict C1 in the logging
    /// facelift: "identical lines dedup to once per battle" applies to the CONSOLE only; the
    /// file sink keeps every occurrence unconditionally). Called from Engine on both the
    /// battle-enter and battle-exit edges via <see cref="ModLogger.NoteBattleEdge"/>.</summary>
    void NoteBattleEdge();
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
