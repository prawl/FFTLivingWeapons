using System;

namespace LivingWeapon;

/// <summary>
/// Logging contract for the runtime -- ported from FFTColorCustomizer's ModLogger/ILogger split
/// (ColorMod/Interfaces/ILogger.cs, ColorMod/Utilities/ModLogger.cs) so both mods share one
/// logging shape. <see cref="ModLogger"/> is the static facade every call site uses;
/// <see cref="FileConsoleLogger"/> is the production implementation and <see cref="NullLogger"/>
/// the test-only swallow-everything one. LogLevel is the programmatic "turn a whole tier on/off"
/// control; Mod.cs pins it to Info at startup (LW-52 removed the VerboseLog launcher knob), and a
/// dev raises it to Debug there when a live diagnosis needs Debug on the console.
/// </summary>
internal interface ILogger
{
    /// <summary>Console volume threshold -- see <see cref="FileConsoleLogger"/> for the two-sink
    /// semantics (the file evidence chain ignores this entirely).</summary>
    LogLevel LogLevel { get; set; }

    // --- Verb-aware entry points (ModLogger's typed facade: Event/Warn/Error/Debug). The FILE
    // line always carries "[verb] "; the CONSOLE line carries it only at Warning/Error tier. An
    // Info console line (the match report's narrative sentences) renders subject-first with no
    // leading bracket. See FileConsoleLogger's class doc for the exact rendering split. The old
    // verb-less members (Log/LogError/LogWarning/LogDebug taking a bare string) were deleted in
    // LW-155 after their call-site migration finished; LogContractTests trips if one returns. ---

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

    /// <summary>Debug-tier, verb-aware. File always carries "[verb] "; when the console is raised to
    /// Debug and surfaces a Debug line, the verb rides along too (a diagnostic tier, not the curated
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
