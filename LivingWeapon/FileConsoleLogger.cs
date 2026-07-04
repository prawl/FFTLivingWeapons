using System;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Production <see cref="ILogger"/>. TWO-SINK semantics -- the one deliberate divergence from
/// FFTColorCustomizer's ConsoleLogger (which gates one console+file tee behind a single
/// threshold): the FILE sink (livingweapon.log, rotated per launch to livingweapon.prev.log the
/// same way the old Log class did, millisecond timestamps, "[FFTLivingWeapons] " prefix) writes
/// EVERY message -- Debug tier included -- UNCONDITIONALLY, tagged "DBG " right after the
/// timestamp. The CONSOLE sink only writes messages at or above <see cref="LogLevel"/>. So
/// LogLevel is a CONSOLE-only volume knob (flipped by Config.VerboseLog, see Mod.cs); the file
/// evidence chain every live diagnosis has leaned on is never thinner than before this overhaul,
/// regardless of the knob.
/// </summary>
internal sealed class FileConsoleLogger : ILogger
{
    private const string Prefix = "[FFTLivingWeapons] ";

    private readonly Action<string> _consoleSink;
    private readonly Action<string> _fileSink;

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Production ctor: rotates modDir/livingweapon.log -> livingweapon.prev.log once,
    /// then wires the real console + file sinks.</summary>
    public FileConsoleLogger(string modDir) : this(SafeConsoleWrite, MakeFileSink(modDir)) { }

    /// <summary>Test seam: inject fake sinks so tests never touch the real console or
    /// filesystem. Internal (not private) -- LivingWeapon.Tests drives this directly.</summary>
    internal FileConsoleLogger(Action<string> consoleSink, Action<string> fileSink)
    {
        _consoleSink = consoleSink;
        _fileSink = fileSink;
    }

    private static void SafeConsoleWrite(string m) { try { Console.WriteLine(m); } catch { } }

    /// <summary>Rotate any prior session's log out of the way, then return a closure that
    /// appends one timestamped line per call. Rotation failures (locked file, read-only mod
    /// dir, ...) degrade to "no file sink" rather than throwing -- console logging must survive
    /// a broken deploy folder.</summary>
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

    public void Log(string message) => Write(LogLevel.Info, "", message);
    public void LogWarning(string message) => Write(LogLevel.Warning, "WARNING: ", message);

    /// <summary>Also arms the flight recorder's FlushOnce error trigger (B1) -- a flag-only
    /// request, no I/O happens on this call/thread. The Engine loop drains it on its next tick
    /// (Flight.DrainPending), so a LogError fired from the game's own SetTextString thread
    /// (PromptSwapHook.Detour) never stalls on disk I/O here.</summary>
    public void LogError(string message)
    {
        Write(LogLevel.Error, "ERROR: ", message);
        Flight.RequestFlush("error");
    }

    public void LogDebug(string message) => Write(LogLevel.Debug, "DBG ", message);

    public void LogError(string message, Exception exception)
    {
        LogError(message);
        if (exception != null)
            Write(LogLevel.Error, "ERROR: ", $"  {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>The two-sink core. File gets every call unconditionally (timestamped, tier-tagged);
    /// console only gets it when <paramref name="level"/> is at or above the configured LogLevel.</summary>
    private void Write(LogLevel level, string tierTag, string message)
    {
        string line = Prefix + tierTag + (message ?? string.Empty);
        try { _fileSink(DateTime.Now.ToString("HH:mm:ss.fff ") + line); } catch { }
        if (level >= LogLevel) { try { _consoleSink(line); } catch { } }
    }
}
