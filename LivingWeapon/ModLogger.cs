using System;

namespace LivingWeapon;

/// <summary>
/// Static logging facade -- ported from FFTColorCustomizer's ModLogger (Utilities/ModLogger.cs).
/// Every call site in the runtime logs through here (ModLogger.Log/.LogError/.LogWarning/.LogDebug),
/// never against <see cref="ILogger"/> or a concrete logger directly. <see cref="Instance"/> is the
/// test seam: <see cref="Reset"/>/<see cref="UseNullLogger"/> swap it, mirroring the injected-sink
/// pattern the rest of the runtime already uses (KillTracker/BattleLog). Production defaults lazily
/// to a <see cref="FileConsoleLogger"/> rooted at the modDir last passed to <see cref="Init"/>
/// (mirrors the old Log.Init timing -- called once from Mod.cs before anything else logs).
/// </summary>
internal static class ModLogger
{
    private static ILogger? _logger;
    private static readonly object _lock = new();
    private static string? _modDir;

    /// <summary>Called once from Mod.cs, before any other logging, so the lazily-created default
    /// logger rotates/writes livingweapon.log in the right place.</summary>
    public static void Init(string modDir)
    {
        lock (_lock) { _modDir = modDir; _logger = null; }
    }

    /// <summary>The active logger. Lazily creates the production <see cref="FileConsoleLogger"/>
    /// on first use if nothing was injected/initialized yet.</summary>
    public static ILogger Instance
    {
        get
        {
            if (_logger == null)
                lock (_lock)
                    _logger ??= new FileConsoleLogger(_modDir ?? Environment.CurrentDirectory);
            return _logger;
        }
        set { lock (_lock) { _logger = value; } }
    }

    /// <summary>Console volume threshold passthrough -- see FileConsoleLogger's two-sink doc.</summary>
    public static LogLevel LogLevel
    {
        get => Instance.LogLevel;
        set => Instance.LogLevel = value;
    }

    // --- Legacy free-form entry points. Every call site OUTSIDE this file, FileConsoleLogger.cs,
    // NullLogger.cs, and the transitional Log.cs shim (no new call sites there) should be using
    // the typed facade below (Event/Warn/Error/Debug/For) instead; LogContractTests ratchets
    // the legacy callers down to that allow-list. Kept public (not made private) because that
    // allow-list is enforced by a source scan, not by the compiler: a hard access-modifier cut
    // would force every one of the ~200 call sites to migrate atomically instead of stage-by-stage.
    public static void Log(string message) => Instance.Log(message);
    public static void LogError(string message) => Instance.LogError(message);
    public static void LogWarning(string message) => Instance.LogWarning(message);
    public static void LogDebug(string message) => Instance.LogDebug(message);
    public static void LogException(string message, Exception exception) => Instance.LogError(message, exception);

    // --- Typed facade. Every module logs through these: a LogVerb names the closed event-verb
    // glossary (docs/LOGGING.md). The FILE line always carries "[verb] "; the CONSOLE line drops
    // it at Info tier only (subject-first match-report prose) and keeps it at Warning/Error tier
    // (bug-report triage); see FileConsoleLogger's class doc for the exact rendering split.
    // Debug is file-only by default. ---

    public static void Event(LogVerb verb, string message) => Instance.Log(verb, message);
    public static void Warn(LogVerb verb, string message) => Instance.LogWarning(verb, message);
    public static void Error(LogVerb verb, string message) => Instance.LogError(verb, message);
    public static void Error(LogVerb verb, string message, Exception exception) => Instance.LogError(verb, message, exception);
    public static void Debug(LogVerb verb, string message) => Instance.LogDebug(verb, message);

    /// <summary>The two-line id pattern (conflict C2): "numeric ids only in parens and only in
    /// file lines" vs. one Write() call reaching both sinks. An Info console line free of ids,
    /// paired with a [trace] Debug companion carrying the parenthesized ids/hex/offsets. Zero
    /// logger-core changes: just two facade calls issued together.</summary>
    public static void EventWithTrace(LogVerb verb, string message, string traceDetail)
    {
        Event(verb, message);
        Debug(LogVerb.Trace, traceDetail);
    }

    /// <summary>Same two-line id pattern, Warning-tier console line.</summary>
    public static void WarnWithTrace(LogVerb verb, string message, string traceDetail)
    {
        Warn(verb, message);
        Debug(LogVerb.Trace, traceDetail);
    }

    /// <summary>A logger scoped to one verb and one "is this module armed this battle" predicate
    /// (e.g. <c>Wielder.AnyDeployedMainHand(_mem, GalewindId)</c>). Implements every audited
    /// GATE-ON-ARMED disposition structurally: see <see cref="ScopedLogger"/>.</summary>
    public static ScopedLogger For(LogVerb verb, Func<bool> armed) => new(verb, armed);

    /// <summary>Console-only per-battle dedup reset (conflict C1): Engine calls this on both
    /// the battle-enter and battle-exit edges. The file sink is never affected.</summary>
    public static void NoteBattleEdge() => Instance.NoteBattleEdge();

    /// <summary>Test-only: drop the current logger so the next call re-creates the default.</summary>
    public static void Reset()
    {
        lock (_lock) { _logger = null; _modDir = null; }
    }

    /// <summary>Test-only: swap in the swallow-everything logger.</summary>
    public static void UseNullLogger() => Instance = NullLogger.Instance;
}
