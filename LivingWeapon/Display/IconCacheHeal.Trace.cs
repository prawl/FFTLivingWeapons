#if LWDEV
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// Dev build only. Heal lines always log, and a marker file turns on the per lookup trace and a once a second cost line.
/// </summary>
internal sealed partial class IconCacheHeal
{
    internal const string MarkerFileName = "lw427_trace.txt";
    private const int MarkerPollEvery = 30;
    private const string IconNeedle = "icon/equip_item";

    private string? _markerPath;
    private volatile bool _tracing;
    private int _pollCounter;
    private readonly HashSet<string> _seenHits = new();
    private long _lookups;
    private long _detourTicks;
    private long _originalTicks;
    private long _originalCalls;
    private long _costWindowStart;

    private void InitTrace(string? modDir)
        => _markerPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, MarkerFileName);

    /// <summary>Polls the marker file every 30th call from the engine tick.</summary>
    public void Tick()
    {
        if (_markerPath == null || ++_pollCounter % MarkerPollEvery != 0) return;
        bool now = File.Exists(_markerPath);
        if (now == _tracing) return;
        _tracing = now;
        if (now) { lock (_seenHits) _seenHits.Clear(); _costWindowStart = Stopwatch.GetTimestamp(); }
        ModLogger.Event(LogVerb.Display, now ? "IconCacheHeal tracing on." : "IconCacheHeal tracing off.");
    }

    private nint CallOriginal(nint manager, nint key)
    {
        if (!_tracing) return _original!(manager, key);
        long t0 = Stopwatch.GetTimestamp();
        nint r = _original!(manager, key);
        Interlocked.Add(ref _originalTicks, Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref _originalCalls);
        return r;
    }

    internal nint Detour(nint manager, nint key)
    {
        if (!_tracing) return DetourBody(manager, key);
        long t0 = Stopwatch.GetTimestamp();
        nint entry = DetourBody(manager, key);
        Interlocked.Add(ref _detourTicks, Stopwatch.GetTimestamp() - t0);
        Interlocked.Increment(ref _lookups);
        try { TraceLookup(manager, key, entry); FlushCost(); }
        catch (Exception) { }
        return entry;
    }

    private void TraceLookup(nint manager, nint key, nint entry)
    {
        string? name = ReadName(key, out _);
        if (name == null || !name.Contains(IconNeedle)) return;
        string kind = IconCacheHealPolicy.IsStackKey(key, _stack()) ? "stack" : "heap";
        if (entry != 0)
        {
            lock (_seenHits) { if (!_seenHits.Add($"{(long)manager:X}|{kind}|{name}")) return; }
        }
        ModLogger.Debug(LogVerb.Display, $"IconCacheHeal lookup cache=0x{(long)manager:X} thread={Environment.CurrentManagedThreadId} key={kind} {IconCacheHealPolicy.Tail(name)} -> {(entry == 0 ? "MISS" : "hit")}");
    }

    private void FlushCost()
    {
        long now = Stopwatch.GetTimestamp();
        long start = Interlocked.Read(ref _costWindowStart);
        if (now - start < Stopwatch.Frequency || Interlocked.CompareExchange(ref _costWindowStart, now, start) != start) return;
        long n = Interlocked.Exchange(ref _lookups, 0);
        long dt = Interlocked.Exchange(ref _detourTicks, 0);
        long oc = Interlocked.Exchange(ref _originalCalls, 0);
        long ot = Interlocked.Exchange(ref _originalTicks, 0);
        if (n == 0) return;
        double perTick = 1e9 / Stopwatch.Frequency;
        ModLogger.Debug(LogVerb.Display, $"IconCacheHeal cost {n} lookups/s, mean {dt * perTick / n:F0} ns detour, mean {(oc == 0 ? 0 : ot * perTick / oc):F0} ns original");
    }

    private static void TraceHeal(nint manager, string name, bool accepted, bool healed)
        => ModLogger.Debug(LogVerb.Display,
            $"IconCacheHeal heal cache=0x{(long)manager:X} thread={Environment.CurrentManagedThreadId} {IconCacheHealPolicy.Tail(name)} loader={(accepted ? "accepted" : "refused")} entry={(healed ? "present" : "missing")}");
}
#endif
