using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>
/// Production <see cref="ILogger"/>. TWO-SINK semantics: the one deliberate divergence from
/// FFTColorCustomizer's ConsoleLogger (which gates one console+file tee behind a single
/// threshold). The FILE sink (livingweapon.log, rotated per launch to livingweapon.prev.log the
/// same way the old Log class did, millisecond timestamps) writes EVERY message, Debug tier
/// included, UNCONDITIONALLY. The CONSOLE sink only writes messages at or above
/// <see cref="LogLevel"/>, and (logging facelift conflict C1) additionally suppresses a line
/// whose (level, verb, message) identity already appeared once this battle; <see cref="NoteBattleEdge"/>
/// resets that seen-set on both battle edges. The FILE sink is never deduped: the evidence chain
/// every live diagnosis has leaned on is never thinner than the console.
///
/// RENDERING SPLIT (owner format delta, post-review): the FILE line always carries the verb:
/// <c>[Living Weapons] [HH:mm:ss.fff] [LEVEL] [verb] description</c>. The CONSOLE line drops the
/// "[verb] " segment at Info tier only, rendering subject-first prose for the match report a
/// player actually reads: <c>[Living Weapons] [HH:mm:ss.fff] [LEVEL] description</c>. Warning and
/// Error console lines keep the verb (a bug-report console paste needs it for triage), and so
/// does a Debug line that reaches the console when the level is raised to Debug (a diagnostic tier, not curated
/// narrative). Legacy verb-less callers (<see cref="Log"/>/<see cref="LogWarning"/>/
/// <see cref="LogError"/>/<see cref="LogDebug"/>, mid-migration call sites ratcheted to zero by
/// LogContractTests) render identically on both sinks, exactly as before this split.
///
/// The console dedup key is the SEMANTIC identity (level, verb, message) computed before any
/// rendering happens, never the rendered console string: two different verbs sharing the same
/// Info-tier sentence render identical console text (the verb is hidden there), but they are
/// different events and both must reach the console.
/// </summary>
internal sealed class FileConsoleLogger : ILogger
{
    private const string Prefix = "[Living Weapons]";

    private readonly Action<string> _consoleSink;
    private readonly Action<string> _fileSink;
    private readonly HashSet<(LogLevel level, LogVerb? verb, string message)> _consoleSeenThisBattle = new();

    /// <summary>Guards the whole body of <see cref="Write"/> and <see cref="NoteBattleEdge"/>.
    /// Two threads call into this logger: the game's own SetTextString thread (PromptSwapHook.Detour
    /// logs mid-toast delivery) and the Engine background loop's own thread (every subsystem's
    /// routine logging, plus NoteBattleEdge itself on both battle edges). <see cref="_consoleSeenThisBattle"/>
    /// is a plain HashSet, not a concurrent collection: Add (from Write) and Clear (from
    /// NoteBattleEdge) racing across those two threads with no lock is undefined behavior, not
    /// merely a race on which entry wins, and it is not hypothetical (an unlocked stress run of
    /// FileConsoleLoggerThreadSafetyTests reliably corrupted the set, throwing
    /// IndexOutOfRangeException out of Clear and InvalidOperationException("...corrupted its
    /// state") out of Add). Locking the entire Write body, not just the HashSet touch, also
    /// keeps a line's file half and console half from interleaving with another thread's line,
    /// and closes a second hazard: two threads calling the production file sink
    /// (File.AppendAllText) at the same moment can collide on the file handle, one throwing a
    /// sharing-violation IOException that Write's own try/catch swallows, silently dropping that
    /// thread's line from livingweapon.log, the one sink every live diagnosis depends on being
    /// complete.</summary>
    private readonly object _gate = new();

    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>Production ctor: rotates modDir/livingweapon.log -> livingweapon.prev.log once,
    /// then wires the real console + file sinks.</summary>
    public FileConsoleLogger(string modDir) : this(SafeConsoleWrite, MakeFileSink(modDir)) { }

    /// <summary>Test seam: inject fake sinks so tests never touch the real console or
    /// filesystem. Internal (not private): LivingWeapon.Tests drives this directly.</summary>
    internal FileConsoleLogger(Action<string> consoleSink, Action<string> fileSink)
    {
        _consoleSink = consoleSink;
        _fileSink = fileSink;
    }

    private static void SafeConsoleWrite(string m) { try { Console.WriteLine(m); } catch { } }

    /// <summary>Rotate any prior session's log out of the way, then return a closure that
    /// appends one timestamped line per call. Rotation failures (locked file, read-only mod
    /// dir, ...) degrade to "no file sink" rather than throwing: console logging must survive
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

    // --- Verb-less (legacy, mid-migration) entry points ---

    public void Log(string message) => Write(LogLevel.Info, null, message);
    public void LogWarning(string message) => Write(LogLevel.Warning, null, message);

    /// <summary>Also arms the flight recorder's FlushOnce error trigger (B1): a flag-only
    /// request, no I/O happens on this call/thread. The Engine loop drains it on its next tick
    /// (Flight.DrainPending), so a LogError fired from the game's own SetTextString thread
    /// (PromptSwapHook.Detour) never stalls on disk I/O here.</summary>
    public void LogError(string message)
    {
        Write(LogLevel.Error, null, message);
        Flight.RequestFlush("error");
    }

    public void LogDebug(string message) => Write(LogLevel.Debug, null, message);

    public void LogError(string message, Exception exception)
    {
        LogError(message);
        if (exception != null)
            Write(LogLevel.Error, null, $"  {exception.GetType().Name}: {exception.Message}");
    }

    // --- Verb-aware entry points (ModLogger's typed facade) ---

    public void Log(LogVerb verb, string message) => Write(LogLevel.Info, verb, message);
    public void LogWarning(LogVerb verb, string message) => Write(LogLevel.Warning, verb, message);

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

    /// <summary>The two-sink core. FILE gets every call unconditionally, always with the verb
    /// bracket when one was supplied. CONSOLE gets a line only when <paramref name="level"/> is
    /// at/above the configured threshold AND the (level, verb, message) identity has not already
    /// been shown once this battle; the console line drops the verb bracket at Info tier only
    /// (see the class doc's rendering split). The whole body runs under <see cref="_gate"/>: see
    /// that field's doc for why (two racing threads, HashSet corruption, the swallowed-write
    /// hazard).</summary>
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
