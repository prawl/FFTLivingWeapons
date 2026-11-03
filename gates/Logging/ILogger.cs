using System;

namespace ModGates.Logging;

/// <summary>What every log call goes through. The mod supplies LogVerb, Trace included.</summary>
internal interface ILogger
{
    /// <summary>The console threshold. The file ignores it and records everything.</summary>
    LogLevel LogLevel { get; set; }

    void Log(LogVerb verb, string message);
    void LogWarning(LogVerb verb, string message);
    void LogError(LogVerb verb, string message);
    void LogError(LogVerb verb, string message, Exception exception);
    void LogDebug(LogVerb verb, string message);

    /// <summary>Clears the console's per battle dedup memory. The file is never deduped.</summary>
    void NoteBattleEdge();
}

/// <summary>Verbosity tiers. A threshold of N lets the console show tier N and above.</summary>
internal enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
    None = 4,
}
