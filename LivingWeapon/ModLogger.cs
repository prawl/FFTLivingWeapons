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

    public static void Log(string message) => Instance.Log(message);
    public static void LogError(string message) => Instance.LogError(message);
    public static void LogWarning(string message) => Instance.LogWarning(message);
    public static void LogDebug(string message) => Instance.LogDebug(message);
    public static void LogException(string message, Exception exception) => Instance.LogError(message, exception);

    /// <summary>Test-only: drop the current logger so the next call re-creates the default.</summary>
    public static void Reset()
    {
        lock (_lock) { _logger = null; _modDir = null; }
    }

    /// <summary>Test-only: swap in the swallow-everything logger.</summary>
    public static void UseNullLogger() => Instance = NullLogger.Instance;
}
