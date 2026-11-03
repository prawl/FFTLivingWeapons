using System;

namespace ModGates.Logging;

/// <summary>Swallows everything, so tests never touch the console or disk.</summary>
internal sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    public LogLevel LogLevel { get; set; } = LogLevel.None;

    public void Log(LogVerb verb, string message) { }
    public void LogWarning(LogVerb verb, string message) { }
    public void LogError(LogVerb verb, string message) { }
    public void LogError(LogVerb verb, string message, Exception exception) { }
    public void LogDebug(LogVerb verb, string message) { }
    public void NoteBattleEdge() { }
}
