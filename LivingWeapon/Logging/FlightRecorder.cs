using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>One flight-recorder entry: a monotonic elapsed-ms stamp (S5), an event type tag, and
/// a free-form payload. Immutable value type -- copying it under the lock (Record/Snapshot/Flush)
/// is a plain struct copy, never a torn read.</summary>
internal readonly struct FlightRecord
{
    public readonly long ElapsedMs;
    public readonly string Type;
    public readonly string Payload;

    public FlightRecord(long elapsedMs, string type, string payload)
    {
        ElapsedMs = elapsedMs;
        Type = type;
        Payload = payload;
    }
}

/// <summary>
/// The flight recorder's INSTANCE core (the "black box") -- see docs/LOGGING.md for the design
/// writeup. A bounded ring (<see cref="Capacity"/> records) of on-change (elapsedMs, type,
/// payload) events; callers append via <see cref="Record"/> (cheap, lock-protected, no I/O ever
/// happens there). The static <see cref="Flight"/> facade (Flight.cs) is the null-object every
/// production call site actually uses; this class is the testable instance behind it (B2 of the
/// stage-C review) -- clock, wall-clock, file-writer, and retention lister/deleter are all
/// injected so the whole ring/flush/retention contract is unit-testable with no real disk or
/// wall-clock (LivingWeapon.Tests/FlightRecorderTests.cs). The pure serialize/retention-selection
/// logic lives in the sibling FlightRecorder.Policy.cs partial (the 200-line refactor seam) --
/// this file is the stateful runtime half: the ring, the lock, and the injected-IO plumbing.
///
/// FLUSH SAFETY (B1): <see cref="Flush"/> swaps the ring out UNDER the lock (grab a linearized
/// copy + reset the live counters) and does the serialize/write/prune OUTSIDE the lock (S3), so
/// the thread calling <see cref="Record"/> (the game/loop thread, or PromptSwap's game-thread
/// delivery) is never blocked on disk I/O. A flush failure is swallowed -- Log.Write-style, never
/// thrown, never routed through ModLogger (a flush triggered by ModLogger.LogError calling back
/// into ModLogger.LogError would recurse). <see cref="RequestFlush"/> only raises a pending flag;
/// the real flush runs later from <see cref="DrainPending"/> (called once per Engine tick), so a
/// flush requested from the game's own SetTextString thread (FileConsoleLogger.LogError, via
/// PromptSwapHook.Detour calling Log.Error before forwarding) never does file I/O on that thread.
/// </summary>
internal sealed partial class FlightRecorder
{
    internal const int Capacity = 4096;
    internal const int RetentionCount = 20;

    private readonly string _flightDir;
    private readonly Func<long> _clock;
    private readonly Func<DateTime> _wallClock;
    private readonly Action<string, string> _fileWriter;
    private readonly Func<string, IEnumerable<string>> _lister;
    private readonly Action<string> _deleter;

    private readonly object _lock = new();
    private readonly FlightRecord[] _ring = new FlightRecord[Capacity];
    private int _head;    // next write index
    private int _count;   // records currently held (<= Capacity)

    private bool _pendingFlush;
    private string _pendingTrigger = "";
    private bool _errorFlushArmed;   // FlushOnce latch (S6/test 6): only the FIRST "error" request per launch flushes

    /// <param name="modDir">Mod deployment directory -- flushes land under modDir/flight/.</param>
    /// <param name="clock">Monotonic elapsedMs source. Default Environment.TickCount64 (S5).</param>
    /// <param name="wallClock">Wall-clock provider for the per-flush-file header anchor (S5).
    /// Default DateTime.Now.</param>
    /// <param name="fileWriter">(path, content) -> write the whole file. Default real IO.</param>
    /// <param name="lister">Existing flight_*.jsonl files under a directory, for retention.
    /// Default real IO (Directory.GetFiles).</param>
    /// <param name="deleter">Deletes one file by path, for retention. Default real IO.</param>
    public FlightRecorder(string modDir, Func<long>? clock = null, Func<DateTime>? wallClock = null,
                           Action<string, string>? fileWriter = null,
                           Func<string, IEnumerable<string>>? lister = null, Action<string>? deleter = null)
    {
        _flightDir = Path.Combine(modDir, "flight");
        _clock = clock ?? (() => Environment.TickCount64);
        _wallClock = wallClock ?? (() => DateTime.Now);
        _fileWriter = fileWriter ?? DefaultWrite;
        _lister = lister ?? DefaultList;
        _deleter = deleter ?? DefaultDelete;
    }

    /// <summary>Records currently held in the ring (test/diagnostic convenience).</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Append one on-change event. Cheap, lock-protected, no I/O -- safe from any
    /// thread. Past <see cref="Capacity"/> the oldest record is silently dropped.</summary>
    public void Record(string type, string payload)
    {
        lock (_lock)
        {
            _ring[_head] = new FlightRecord(_clock(), type ?? "", payload ?? "");
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Ring contents in insertion (oldest-first) order, without clearing it. Test seam
    /// only -- production code never peeks, it only flushes.</summary>
    internal FlightRecord[] Snapshot() { lock (_lock) return Linearize(); }

    /// <summary>Flush the ring to a new .jsonl file under modDir/flight/, then prune retention.
    /// Grabs a linearized copy and resets the live ring UNDER the lock (S3), then serializes,
    /// writes, and prunes OUTSIDE it. An empty ring flushes nothing (no file written). Never
    /// throws and never calls ModLogger (B1) -- every failure here is swallowed.</summary>
    public void Flush(string trigger)
    {
        FlightRecord[] snapshot;
        lock (_lock)
        {
            if (_count == 0) return;
            snapshot = Linearize();
            _head = 0;
            _count = 0;
        }
        try
        {
            DateTime wall = _wallClock();
            long flushElapsed = _clock();
            string path = Path.Combine(_flightDir, $"flight_{wall:yyyyMMdd_HHmmss}_{SafeTrigger(trigger)}.jsonl");
            _fileWriter(path, Serialize(snapshot, wall, flushElapsed));
            PruneRetention();
        }
        catch { /* swallow -- a flush failure must never throw or call ModLogger (B1: re-entrancy) */ }
    }

    /// <summary>Raises a pending-flush flag ONLY -- no I/O happens on this call (B1). The
    /// "error" trigger is FlushOnce: once the first-ever error request has been made, every later
    /// one is a full no-op (it does not even re-arm the pending flag) -- only the FIRST live-error
    /// of a launch ever produces a flush file, however many LogError calls follow.</summary>
    public void RequestFlush(string trigger)
    {
        lock (_lock)
        {
            if (trigger == "error")
            {
                if (_errorFlushArmed) return;
                _errorFlushArmed = true;
            }
            _pendingFlush = true;
            _pendingTrigger = trigger;
        }
    }

    /// <summary>Performs whatever flush <see cref="RequestFlush"/> queued. Called from Engine's
    /// own loop tick -- never from the thread that requested it. Cheap flag check when idle.</summary>
    public void DrainPending()
    {
        bool due;
        string trigger;
        lock (_lock)
        {
            due = _pendingFlush;
            trigger = _pendingTrigger;
            _pendingFlush = false;
        }
        if (due) Flush(trigger);
    }

    // ---- internals ----

    /// <summary>Lists existing archives, hands the pure selection off to
    /// <see cref="SelectForDeletion"/> (FlightRecorder.Policy.cs), then deletes what it picked.</summary>
    private void PruneRetention()
    {
        try
        {
            var files = _lister(_flightDir) ?? Array.Empty<string>();
            foreach (var f in SelectForDeletion(files, RetentionCount))
            {
                try { _deleter(f); } catch { }
            }
        }
        catch { }
    }

    private static void DefaultWrite(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content);
    }

    private static IEnumerable<string> DefaultList(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "flight_*.jsonl") : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void DefaultDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
