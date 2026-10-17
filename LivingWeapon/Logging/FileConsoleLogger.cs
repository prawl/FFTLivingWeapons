using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Production <see cref="ILogger"/> with two sinks. The file sink (livingweapon.log, rotated per launch to
/// livingweapon.prev.log) gets every message, Debug included. The console sink gets messages at or above
/// <see cref="LogLevel"/> and skips a repeat of the same (level, verb, message) until <see cref="NoteBattleEdge"/>
/// clears the seen-set (logging facelift C1). File lines carry the verb. Info console lines omit it so the match
/// report reads as plain prose, while Warning, Error and raised-level Debug console lines keep it for triage.
/// Dedup keys on the (level, verb, message) identity, since two verbs can render the same Info text.
/// </summary>
internal sealed class FileConsoleLogger : ILogger
{
    private const string Prefix = "[Living Weapons]";

    private readonly Action<string> _consoleSink;
    private readonly Action<string> _fileSink;
    private readonly HashSet<(LogLevel level, LogVerb? verb, string message)> _consoleSeenThisBattle = new();

    /// <summary>Guards <see cref="Write"/> and <see cref="NoteBattleEdge"/>. The game's SetTextString thread
    /// (PromptSwapHook.Detour) and the Engine loop thread both log, and a racing Add and Clear corrupted the
    /// HashSet in FileConsoleLoggerThreadSafetyTests. Locking the whole body also keeps a line's file and
    /// console halves together and stops concurrent File.AppendAllText calls from dropping a line on a
    /// sharing violation.</summary>
    private readonly object _gate = new();

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Rotates modDir/livingweapon.log to livingweapon.prev.log, then wires the console and file sinks.</summary>
    public FileConsoleLogger(string modDir) : this(SafeConsoleWrite, MakeFileSink(modDir)) { }

    /// <summary>Test seam that takes fake sinks. Internal so LivingWeapon.Tests can call it.</summary>
    internal FileConsoleLogger(Action<string> consoleSink, Action<string> fileSink)
    {
        _consoleSink = consoleSink;
        _fileSink = fileSink;
    }

    private static void SafeConsoleWrite(string m) { try { Console.WriteLine(m); } catch { } }

    /// <summary>Rotates the prior session's log, then returns a sink that appends one timestamped line per call.
    /// A failed rotation (locked file, read-only mod dir) leaves no file sink so console logging survives.</summary>
    private static Action<string> MakeFileSink(string modDir)
    {
        string file = Path.Combine(modDir, "livingweapon.log");
        try
        {
            if (File.Exists(file))
                File.Move(file, Path.Combine(modDir, "livingweapon.prev.log"), true);
        }
        catch { }
        return line => { try { File.AppendAllText(file, line + "\n"); } catch { } };
    }

    // --- Verb-aware entry points (ModLogger's typed facade) ---

    public void Log(LogVerb verb, string message) => Write(LogLevel.Info, verb, message);
    public void LogWarning(LogVerb verb, string message) => Write(LogLevel.Warning, verb, message);

    /// <summary>Also arms the flight recorder's FlushOnce error trigger (B1). It only sets a flag; Engine drains it
    /// via Flight.DrainPending so the game's SetTextString thread never waits on disk.</summary>
    public void LogError(LogVerb verb, string message)
    {
        Write(LogLevel.Error, verb, message);
        Flight.RequestFlush("error");
    }

    public void LogDebug(LogVerb verb, string message) => Write(LogLevel.Debug, verb, message);

    public void LogError(LogVerb verb, string message, Exception exception)
    {
        LogError(verb, message);
        if (exception != null)
            Write(LogLevel.Error, verb, $"  {exception.GetType().Name}: {exception.Message}");
    }

    /// <inheritdoc/>
    public void NoteBattleEdge()
    {
        lock (_gate) { _consoleSeenThisBattle.Clear(); }
    }

    /// <summary>The two-sink core. The file always gets the line. The console gets it when the level passes the
    /// threshold and the identity is new this battle. Runs under <see cref="_gate"/>.</summary>
    private void Write(LogLevel level, LogVerb? verb, string message)
    {
        lock (_gate)
        {
            string levelToken = LevelToken(level);
            string body = message ?? string.Empty;
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            string verbBracket = verb.HasValue ? $"[{verb.Value.Token()}] " : "";

            string fileLine = $"{Prefix} [{timestamp}] [{levelToken}] {verbBracket}{body}";
            try { _fileSink(fileLine); } catch { }

            if (level >= LogLevel && _consoleSeenThisBattle.Add((level, verb, body)))
            {
                bool showVerbOnConsole = verb.HasValue && level != LogLevel.Info;
                string consoleLine = $"{Prefix} [{timestamp}] [{levelToken}] {(showVerbOnConsole ? verbBracket : "")}{body}";
                try { _consoleSink(consoleLine); } catch { }
            }
        }
    }

    private static string LevelToken(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => level.ToString().ToUpperInvariant(),
    };
}
