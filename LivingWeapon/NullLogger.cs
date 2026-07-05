using System;

namespace LivingWeapon;

/// <summary>Null-object <see cref="ILogger"/>: swallows everything. <see cref="ModLogger.UseNullLogger"/>
/// swaps this in so tests exercising code that logs never touch the real console or filesystem.</summary>
internal sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    public LogLevel LogLevel { get; set; } = LogLevel.None;

    public void Log(string message) { }
    public void LogError(string message) { }
    public void LogError(string message, Exception exception) { }
    public void LogWarning(string message) { }
    public void LogDebug(string message) { }
    public void Log(LogVerb verb, string message) { }
    public void LogWarning(LogVerb verb, string message) { }
    public void LogError(LogVerb verb, string message) { }
    public void LogError(LogVerb verb, string message, Exception exception) { }
    public void LogDebug(LogVerb verb, string message) { }
    public void NoteBattleEdge() { }
}
