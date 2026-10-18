using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// LW-427 research tap. Logs every menu icon cache lookup and load the game performs, with the
/// manager each screen asks, so the lookup that comes back empty on a screen that never reloads
/// can be named. Dev build only. Silent until lw427_trace.txt exists in the mod folder.
/// </summary>
internal sealed class IconLookupSpike
{
    internal const string MarkerFileName = "lw427_trace.txt";
    private const string IconNeedle = "icon/equip_item";
    private const int MaxNameBytes = 120;
    private const int MarkerPollEvery = 30;

    private static long FnLookup => ImageBase.At(0x1403FBB80);
    private static long FnLoad => ImageBase.At(0x1403FBE3C);

    // mov [rsp+8],rbx; mov rax,[rcx+0E0h]; read from the exe on 2026-10-18.
    internal static readonly byte[] LookupPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x8B, 0x81, 0xE0, 0x00, 0x00, 0x00 };
    // mov [rsp+18h],rbx; push rbp; push rsi; push rdi; sub rsp,140h; read the same day.
    internal static readonly byte[] LoadPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x55, 0x56, 0x57, 0x48, 0x81, 0xEC, 0x40, 0x01, 0x00, 0x00 };

    [Function(CallingConventions.Microsoft)]
    private delegate nint LookupFn(nint manager, nint key);

    [Function(CallingConventions.Microsoft)]
    private delegate nint LoadFn(nint manager, nint uri, byte flag);

    private readonly IGameMemory _mem;
    private readonly string? _markerPath;
    private readonly HashSet<string> _seenHits = new();
    private readonly object _gate = new();
    private IHook<LookupFn>? _lookupHook;
    private IHook<LoadFn>? _loadHook;
    private LookupFn? _lookupKeepalive;
    private LoadFn? _loadKeepalive;
    private volatile bool _tracing;
    private int _pollCounter;

    public IconLookupSpike(IGameMemory mem, string? modDir)
    {
        _mem = mem;
        _markerPath = string.IsNullOrEmpty(modDir) ? null : Path.Combine(modDir, MarkerFileName);
    }

    internal bool Tracing => _tracing;

    /// <summary>Install both detours behind their prologue checks. Idempotent.</summary>
    public void Arm(IReloadedHooks? hooks)
    {
        if (hooks == null || _lookupHook != null) return;
        if (!PrologueMatches(FnLookup, LookupPrologue) || !PrologueMatches(FnLoad, LoadPrologue))
        {
            ModLogger.Warn(LogVerb.Display, "IconLookupSpike stays off; the icon cache lookup or loader is not where this build expects it.");
            return;
        }
        try
        {
            _lookupKeepalive = LookupDetour;
            _loadKeepalive = LoadDetour;
            _lookupHook = hooks.CreateHook<LookupFn>(_lookupKeepalive, FnLookup).Activate();
            _loadHook = hooks.CreateHook<LoadFn>(_loadKeepalive, FnLoad).Activate();
            ModLogger.Event(LogVerb.Display, $"IconLookupSpike armed; drop {MarkerFileName} into the mod folder to trace icon cache lookups.");
        }
        catch (Exception ex)
        {
            ModLogger.Warn(LogVerb.Display, $"IconLookupSpike could not hook the icon cache ({ex.Message}).");
        }
    }

    private bool PrologueMatches(long address, byte[] expected)
        => _mem.TryReadBytes(address, expected.Length, out var actual) && HookLandmark.Verify(actual, expected);

    /// <summary>Polls the marker file every 30th call; the detours read the flag.</summary>
    public void Tick()
    {
        if (_markerPath == null || ++_pollCounter % MarkerPollEvery != 0) return;
        bool now = File.Exists(_markerPath);
        if (now == _tracing) return;
        _tracing = now;
        if (now) lock (_gate) _seenHits.Clear();
        ModLogger.Event(LogVerb.Display, now ? "IconLookupSpike tracing on." : "IconLookupSpike tracing off.");
    }

    private nint LookupDetour(nint manager, nint key)
    {
        nint entry = _lookupHook!.OriginalFunction(manager, key);
        if (!_tracing) return entry;
        try
        {
            if (!_mem.TryReadBytes(key, 12, out var keyBytes) || keyBytes.Length < 12) return entry;
            long namePtr = BitConverter.ToInt64(keyBytes, 0);
            uint id = BitConverter.ToUInt32(keyBytes, 8);
            string? name = ReadName(namePtr);
            if (name == null || !name.Contains(IconNeedle)) return entry;
            string line = $"lookup mgr=0x{(long)manager:X} id=0x{id:X} {Tail(name)} -> " + (entry == 0 ? "MISS" : $"hit entry=0x{(long)entry:X}");
            if (entry != 0)
            {
                lock (_gate) { if (!_seenHits.Add($"{(long)manager:X}|{name}")) return entry; }
            }
            ModLogger.Debug(LogVerb.Display, "IconLookupSpike " + line);
        }
        catch (Exception ex)
        {
            ModLogger.Debug(LogVerb.Display, $"IconLookupSpike lookup tap faulted ({ex.GetType().Name}); the game call already returned.");
        }
        return entry;
    }

    private nint LoadDetour(nint manager, nint uri, byte flag)
    {
        nint entry = _loadHook!.OriginalFunction(manager, uri, flag);
        if (!_tracing) return entry;
        try
        {
            string? name = ReadName(uri);
            if (name == null || !name.Contains(IconNeedle)) return entry;
            ModLogger.Debug(LogVerb.Display, $"IconLookupSpike LOAD mgr=0x{(long)manager:X} flag={flag} {Tail(name)} -> entry=0x{(long)entry:X}");
        }
        catch (Exception ex)
        {
            ModLogger.Debug(LogVerb.Display, $"IconLookupSpike load tap faulted ({ex.GetType().Name}); the game call already returned.");
        }
        return entry;
    }

    private string? ReadName(long ptr)
    {
        if (ptr == 0 || !_mem.TryReadBytes(ptr, MaxNameBytes, out var raw)) return null;
        int end = Array.IndexOf(raw, (byte)0);
        if (end < 0) end = raw.Length;
        return Encoding.ASCII.GetString(raw, 0, end);
    }

    /// <summary>The part of the URI after the last slash, enough to name the icon and surface.</summary>
    internal static string Tail(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }
}
