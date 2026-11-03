using System;
using System.Collections.Generic;
using ModGates.Logging;

namespace ModGates.Tests.Logging;

/// <summary>
/// Captures every line the facade would have written while the scope is open, then restores
/// the logger that was there before. Builds the real FileConsoleLogger with injected sinks, so
/// what the test sees is what the file and console would have received.
/// </summary>
internal sealed class LogCapture : IDisposable
{
    public const string Prefix = "[Test Mod]";

    public List<string> Console { get; } = new();
    public List<string> File { get; } = new();

    private readonly ILogger _prior;

    public static LogCapture Start(LogLevel level = LogLevel.Info, bool console = true, bool file = true)
        => new(level, console, file);

    private LogCapture(LogLevel level, bool console, bool file)
    {
        _prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(console ? Console.Add : _ => { }, file ? File.Add : _ => { }, Prefix) { LogLevel = level };
    }

    public void Dispose() => ModLogger.Instance = _prior;
}
