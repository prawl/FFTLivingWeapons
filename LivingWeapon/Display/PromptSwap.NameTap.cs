using System;
using System.Collections.Generic;
#if LWDEV
using System.Diagnostics;
using System.IO;
using System.Threading;
#endif

namespace LivingWeapon;

internal sealed partial class PromptSwap
{
#if LWDEV
    internal const string NameTapMarkerFileName = "lw444_nametap.txt";
    private const int NameTapPollEvery = 30;
    private const int HeadCap = 32;
    private const int HitCap = 64;

    private string? _tapMarkerPath;
    private volatile bool _tapping;
    private int _tapPoll;
    private IReadOnlyList<(int Id, string Name)> _tapNames = Array.Empty<(int, string)>();
    private readonly object _tapLock = new();
    private readonly HashSet<string> _tapHits = new();
    private readonly HashSet<string> _tapHeads = new();
    private int _tapLogged;
    private bool _tapCapNoted;
    private long _tapCalls;
    private long _tapReadable;
    private long _tapTokens;
    private long _tapHitCount;
    private long _tapUtf16Count;
    private long _tapTicks;
    private long _tapWindowStart;

    internal void EnableNameTap(IReadOnlyDictionary<int, WeaponMeta> meta, string? modDir)
    {
        _tapNames = NameTapPolicy.Order(meta);
        _tapMarkerPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, NameTapMarkerFileName);
    }

    /// <summary>Turns the trace on or off when the owner adds or deletes the marker file, checked every 30th engine tick.</summary>
    public void TickNameTap()
    {
        if (_tapMarkerPath == null || ++_tapPoll % NameTapPollEvery != 0) return;
        bool now = File.Exists(_tapMarkerPath);
        if (now == _tapping) return;
        if (now)
        {
            lock (_tapLock) { _tapHits.Clear(); _tapHeads.Clear(); _tapLogged = 0; _tapCapNoted = false; }
            _tapCalls = _tapReadable = _tapTokens = _tapHitCount = _tapUtf16Count = _tapTicks = 0;
            _tapWindowStart = Stopwatch.GetTimestamp();
        }
        _tapping = now;
        ModLogger.Event(LogVerb.Toast, now ? "Name tap tracing on." : "Name tap tracing off.");
    }

    /// <summary>Logs what the text setter carries; a fault here must never reach the detour.</summary>
    internal void TapName(nint holder, nint text)
    {
        if (!_tapping || text == 0) return;
        long t0 = Stopwatch.GetTimestamp();
        try { TapBody(holder, text); }
        catch (Exception) { }
        Interlocked.Add(ref _tapTicks, Stopwatch.GetTimestamp() - t0);
    }

    private void TapBody(nint holder, nint text)
    {
        Interlocked.Increment(ref _tapCalls);
        int firstLen = IconCacheHealPolicy.FirstReadLength(text);
        if (!_mem.TryReadBytes(text, firstLen, out var first)) { FlushNameTapCost(); return; }
        // Text stored two bytes per letter has a zero after every plain letter, so a zero here does not mean the text ended.
        byte[]? second = null;
        if (first.Length < IconCacheHealPolicy.MaxNameBytes)
            _mem.TryReadBytes(text + first.Length, IconCacheHealPolicy.MaxNameBytes - first.Length, out second);
        TapUtf16(holder, second == null ? first : Concat(first, second));
        TapAscii(holder, first, second);
        FlushNameTapCost();
    }

    private void TapUtf16(nint holder, byte[] raw)
    {
        if (!NameTapPolicy.TryMatchUtf16(raw, _tapNames, out int id, out int offset, out string tail)) return;
        Interlocked.Increment(ref _tapUtf16Count);
        int count = Math.Min(ByteScan.Utf16(NameOf(id)).Length + 4, raw.Length - offset);
        string hex = Convert.ToHexString(raw, offset, count);
        LogHit($"{(long)holder}|{id}|utf16",
            n => $"name tap utf16 hit {n}: id={id} tail=\"{tail}\" holder=0x{(long)holder:X} thread={Environment.CurrentManagedThreadId} bytes={hex}");
    }

    private string NameOf(int id)
    {
        foreach (var (nameId, name) in _tapNames) if (nameId == id) return name;
        return "";
    }

    private void TapAscii(nint holder, byte[] first, byte[]? second)
    {
        string? text = IconCacheHealPolicy.NameFrom(first, second);
        if (text == null) return;
        Interlocked.Increment(ref _tapReadable);
        if (NameTapPolicy.IsTokenHead(text)) Interlocked.Increment(ref _tapTokens);
        string clipped = NameTapPolicy.Clip(text);
        string? head = null;
        lock (_tapLock)
        {
            // Two byte text reads as its first letter alone here; skipping it keeps the 32 sample slots for real text.
            if (clipped.Length >= 2 && _tapHeads.Count < HeadCap && _tapHeads.Add(clipped))
                head = $"name tap head {_tapHeads.Count} of {HeadCap}: \"{clipped}\"";
        }
        if (head != null) ModLogger.Debug(LogVerb.Toast, head);
        if (!NameTapPolicy.TryMatch(text, _tapNames, out int id, out string tail)) return;
        Interlocked.Increment(ref _tapHitCount);
        LogHit($"{(long)holder}|{id}|{text}",
            n => $"name tap hit {n}: id={id} tail=\"{tail}\" holder=0x{(long)holder:X} thread={Environment.CurrentManagedThreadId} text=\"{clipped}\"");
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var all = new byte[a.Length + b.Length];
        a.CopyTo(all, 0);
        b.CopyTo(all, a.Length);
        return all;
    }

    private void LogHit(string key, Func<int, string> line)
    {
        string? msg = null;
        lock (_tapLock)
        {
            if (_tapLogged >= HitCap)
            {
                if (!_tapCapNoted) { _tapCapNoted = true; msg = "name tap hit cap reached; counting continues in the cost line"; }
            }
            else if (_tapHits.Add(key)) msg = line(++_tapLogged);
        }
        if (msg != null) ModLogger.Debug(LogVerb.Toast, msg);
    }

    private void FlushNameTapCost()
    {
        long now = Stopwatch.GetTimestamp();
        long start = Interlocked.Read(ref _tapWindowStart);
        if (now - start < Stopwatch.Frequency || Interlocked.CompareExchange(ref _tapWindowStart, now, start) != start) return;
        long calls = Interlocked.Exchange(ref _tapCalls, 0);
        long readable = Interlocked.Exchange(ref _tapReadable, 0);
        long tokens = Interlocked.Exchange(ref _tapTokens, 0);
        long hits = Interlocked.Exchange(ref _tapHitCount, 0);
        long wide = Interlocked.Exchange(ref _tapUtf16Count, 0);
        long ticks = Interlocked.Exchange(ref _tapTicks, 0);
        if (calls == 0) return;
        double ns = ticks * (1e9 / Stopwatch.Frequency) / calls;
        double seconds = (now - start) / (double)Stopwatch.Frequency;
        ModLogger.Debug(LogVerb.Toast, $"name tap {calls} setter calls in {seconds:F1} s, {readable} readable, {tokens} token heads, {hits} name hits, {wide} utf16 hits, mean {ns:F0} ns");
    }
#else
    internal void EnableNameTap(IReadOnlyDictionary<int, WeaponMeta> meta, string? modDir) { }
    public void TickNameTap() { }
    internal void TapName(nint holder, nint text) { }
#endif
}
