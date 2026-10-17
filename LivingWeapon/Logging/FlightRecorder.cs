using System;
using System.Collections.Generic;
using System.IO;

namespace LivingWeapon;

/// <summary>One flight-recorder entry: elapsed ms, a type tag and a free-form payload. Immutable,
/// so copies taken under the lock are never torn.</summary>
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
/// Flight recorder core (the "black box"); design in docs/LOGGING.md. A bounded ring of
/// <see cref="Capacity"/> events; <see cref="Record"/> takes the lock and does no I/O. The static
/// <see cref="Flight"/> facade (Flight.cs) fronts it; clocks and IO are injected so
/// LivingWeapon.Tests/FlightRecorderTests.cs needs no disk, and FlightRecorder.Policy.cs holds the
/// pure serialize/retention logic. <see cref="Flush"/> swaps the ring out under the lock and writes
/// outside it; failures are swallowed and never reach ModLogger, which would recurse. RequestFlush
/// only sets a flag, so a request from the game's SetTextString thread does no file I/O there.
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
    private bool _errorFlushArmed;   // only the first "error" request per launch flushes

    /// <param name="modDir">Mod deployment directory; flushes land under modDir/flight/.</param>
    /// <param name="clock">Monotonic elapsedMs source. Default Environment.TickCount64.</param>
    /// <param name="wallClock">Wall-clock source for the per-file header. Default DateTime.Now.</param>
    /// <param name="fileWriter">(path, content) writer for the whole file. Default real IO.</param>
    /// <param name="lister">Lists existing flight_*.jsonl files for retention. Default Directory.GetFiles.</param>
    /// <param name="deleter">Deletes one file for retention. Default real IO.</param>
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

    /// <summary>Records currently held in the ring.</summary>
    public int Count { get { lock (_lock) return _count; } }

    /// <summary>Append one event. Lock-protected, no I/O, safe from any thread. Past
    /// <see cref="Capacity"/> the oldest record is dropped.</summary>
    public void Record(string type, string payload)
    {
        lock (_lock)
        {
            _ring[_head] = new FlightRecord(_clock(), type ?? "", payload ?? "");
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    /// <summary>Ring contents oldest-first, without clearing it. Test seam only.</summary>
    internal FlightRecord[] Snapshot() { lock (_lock) return Linearize(); }

    /// <summary>Flush the ring to a new .jsonl file under modDir/flight/, then prune retention.
    /// An empty ring writes nothing. Never throws and never calls ModLogger.</summary>
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

    /// <summary>Raises the pending-flush flag, no I/O. The "error" trigger flushes once per launch;
    /// later error requests are no-ops.</summary>
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

    /// <summary>Runs the flush <see cref="RequestFlush"/> queued. Called from Engine's loop tick,
    /// not from the requesting thread.</summary>
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

    /// <summary>Deletes the archives <see cref="SelectForDeletion"/> (FlightRecorder.Policy.cs)
    /// picks from the existing listing.</summary>
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
