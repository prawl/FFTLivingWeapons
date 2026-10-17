using System;

namespace LivingWeapon;

/// <summary>
/// Static logging facade, ported from FFTColorCustomizer's ModLogger (Utilities/ModLogger.cs).
/// Runtime code logs through here or through a <see cref="ScopedLogger"/> from <see cref="For"/>.
/// <see cref="Instance"/> is the test seam (<see cref="Reset"/>, <see cref="UseNullLogger"/>). By default
/// it lazily builds a <see cref="FileConsoleLogger"/> in the modDir given to <see cref="Init"/>.
/// </summary>
internal static class ModLogger
{
    private static ILogger? _logger;
    private static readonly object _lock = new();
    private static string? _modDir;

    /// <summary>Called once from Mod.cs before any other logging, so livingweapon.log lands in the right place.</summary>
    public static void Init(string modDir)
    {
        lock (_lock) { _modDir = modDir; _logger = null; }
    }

    /// <summary>The active logger; creates the production <see cref="FileConsoleLogger"/> on first use.</summary>
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

    /// <summary>Console volume threshold; see FileConsoleLogger's two-sink doc.</summary>
    public static LogLevel LogLevel
    {
        get => Instance.LogLevel;
        set => Instance.LogLevel = value;
    }

    // Typed facade over the verb glossary in docs/LOGGING.md. The file line always carries "[verb] ";
    // the console drops it at Info and keeps it at Warning/Error (see FileConsoleLogger). Debug is file-only by default.

    public static void Event(LogVerb verb, string message) => Instance.Log(verb, message);
    public static void Warn(LogVerb verb, string message) => Instance.LogWarning(verb, message);
    public static void Error(LogVerb verb, string message) => Instance.LogError(verb, message);
    public static void Error(LogVerb verb, string message, Exception exception) => Instance.LogError(verb, message, exception);
    public static void Debug(LogVerb verb, string message) => Instance.LogDebug(verb, message);

    /// <summary>Conflict C2 in the logging ledger. An id-free Info console line plus a [trace] Debug line
    /// carrying the ids, hex and offsets, so the ids reach only the file sink.</summary>
    public static void EventWithTrace(LogVerb verb, string message, string traceDetail)
    {
        Event(verb, message);
        Debug(LogVerb.Trace, traceDetail);
    }

    /// <summary>Same pairing, with a Warning-tier console line.</summary>
    public static void WarnWithTrace(LogVerb verb, string message, string traceDetail)
    {
        Warn(verb, message);
        Debug(LogVerb.Trace, traceDetail);
    }

    /// <summary>A logger scoped to one verb and an "armed this battle" predicate, e.g.
    /// <c>Wielder.AnyDeployedMainHand(_mem, GalewindId)</c>. See <see cref="ScopedLogger"/>.</summary>
    public static ScopedLogger For(LogVerb verb, Func<bool> armed) => new(verb, armed);

    /// <summary>Resets the console-only per-battle dedup (conflict C1). Engine calls it on battle enter and exit.</summary>
    public static void NoteBattleEdge() => Instance.NoteBattleEdge();

    /// <summary>Test-only: drop the current logger so the next call re-creates the default.</summary>
    public static void Reset()
    {
        lock (_lock) { _logger = null; _modDir = null; }
    }

    /// <summary>Test-only: swap in the swallow-everything logger.</summary>
    public static void UseNullLogger() => Instance = NullLogger.Instance;
}
