using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Heals a hidden menu icon the first time a screen draws it, using the game's own loader.
/// Hooks the icon cache lookup and forwards every call. The loader is only called on that screen's cache, never hooked.
/// A native fault inside the loader cannot be caught here, so the prologue checks gate the install.
/// </summary>
internal sealed partial class IconCacheHeal : IIconCacheHealer
{
    private static long FnLookup => ImageBase.At(0x1403FBB80);
    private static long FnLoad => ImageBase.At(0x1403FBE3C);

    // mov [rsp+8],rbx; mov rax,[rcx+0E0h]
    internal static readonly byte[] LookupPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x8B, 0x81, 0xE0, 0x00, 0x00, 0x00 };
    // mov [rsp+18h],rbx; push rbp; push rsi; push rdi; sub rsp,140h
    internal static readonly byte[] LoadPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x18, 0x55, 0x56, 0x57, 0x48, 0x81, 0xEC, 0x40, 0x01, 0x00, 0x00 };

    [Function(CallingConventions.Microsoft)]
    internal delegate nint LookupFn(nint manager, nint key);

    // The game returns a bool in al only, so the delegate must not read the rest of rax.
    [Function(CallingConventions.Microsoft)]
    internal delegate byte LoadFn(nint manager, nint uri, byte flag);

    [DllImport("kernel32.dll")]
    private static extern void GetCurrentThreadStackLimits(out nuint low, out nuint high);

    [ThreadStatic] private static bool _healing;

    private readonly IGameMemory _mem;
    private readonly Func<(long Low, long High)> _stack;
    private readonly object _lock = new();
    private readonly HashSet<string> _tracked = new();
    private readonly Dictionary<(nint Manager, string Name), int> _attempts = new();
    private volatile int _trackedCount;
    private volatile bool _installed;
    private volatile bool _fired;
    private int _warned;
    private int _healed;
    private int _refused;
    private IHook<LookupFn>? _hook;
    private LookupFn? _keepalive;
    private Func<nint, nint, nint>? _original;
    private Func<nint, nint, byte, byte>? _load;

    internal IconCacheHeal(IGameMemory mem, Func<(long Low, long High)>? stack = null, string? modDir = null)
    {
        _mem = mem;
        _stack = stack ?? ThreadStackLimits;
        InitTrace(modDir);
    }

    public bool Armed => _installed && _fired;
    // Test seam, binding a native address to a delegate faults the runtime for a fake address.
    internal Func<long, Func<nint, nint, byte, byte>> LoadBinder { get; set; } = address =>
    {
        var native = Marshal.GetDelegateForFunctionPointer<LoadFn>((nint)address);
        return (m, u, f) => native(m, u, f);
    };

    internal bool HookStored => _hook != null;
    internal int TrackedCount => _trackedCount;
    internal int Healed => Volatile.Read(ref _healed);
    internal int Refused => Volatile.Read(ref _refused);

    private static (long Low, long High) ThreadStackLimits()
    {
        GetCurrentThreadStackLimits(out var low, out var high);
        return ((long)low, (long)high);
    }

    public void Track(IEnumerable<string> partsUris)
    {
        lock (_lock) _trackedCount = IconCacheHealPolicy.AddTracked(_tracked, partsUris);
    }

    /// <summary>Installs the lookup detour behind both prologue checks. Idempotent, and null hooks leaves it off.</summary>
    public void Arm(IReloadedHooks? hooks)
    {
        if (_hook != null) return;
        if (hooks == null)
        {
            ModLogger.Event(LogVerb.Display, "The icon cache hook is not installed, same session glow rings are off this launch, the hooks helper is missing.");
            return;
        }
        try
        {
            if (!ProloguesMatch())
            {
                ModLogger.Warn(LogVerb.Display, "IconCacheHeal stays off; the icon cache lookup or loader is not where this build expects it.");
                return;
            }
            _load = LoadBinder(FnLoad);
            _keepalive = Detour;
            var created = hooks.CreateHook<LookupFn>(_keepalive, FnLookup);
            // Capture the local, a lookup can fire before the field below is stored.
            _original = (m, k) => created.OriginalFunction(m, k);
            _hook = created.Activate();
            _installed = true;
        }
        catch (Exception ex)
        {
            ModLogger.Warn(LogVerb.Display, $"IconCacheHeal could not hook the icon cache ({ex.Message}).");
        }
    }

    internal bool ProloguesMatch() => Matches(FnLookup, LookupPrologue) && Matches(FnLoad, LoadPrologue);

    private bool Matches(long address, byte[] expected)
        => _mem.TryReadBytes(address, expected.Length, out var actual) && HookLandmark.Verify(actual, expected);

    internal void BindForTest(Func<nint, nint, nint> original, Func<nint, nint, byte, byte> load)
    {
        _original = original;
        _load = load;
        _installed = true;
    }

    /// <summary>The hook body. Always forwards to the game's lookup, then heals a tracked miss from a drawing screen.</summary>
    private nint DetourBody(nint manager, nint key)
    {
        nint entry = CallOriginal(manager, key);
        if (!_fired)
        {
            try { NoteFirstCall(); }
            catch (Exception) { }
        }
        if (_healing) return entry;
        try { return Guarded(manager, key, entry); }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
                ModLogger.Warn(LogVerb.Display, $"IconCacheHeal hit an internal error and left the icon alone ({ex.GetType().Name}).");
            return entry;
        }
    }

    private void NoteFirstCall()
    {
        lock (_lock) { if (_fired) return; _fired = true; }
        ModLogger.Event(LogVerb.Display, "The icon cache hook is confirmed working; new glow rings can show in the same session.");
    }

    private nint Heal(nint manager, nint key)
    {
        string? name = ReadName(key, out nint namePtr);
        if (name == null) return 0;
        lock (_lock)
        {
            if (!_tracked.Contains(name)) return 0;
            nint again = CallOriginal(manager, key);
            if (again != 0) return again;
            if (BuilderBusy(manager, name)) return 0;
            _attempts.TryGetValue((manager, name), out int tries);
            if (IconCacheHealPolicy.AttemptsExhausted(tries)) return 0;
            _attempts[(manager, name)] = ++tries;

            _healing = true;
            bool accepted;
            try { accepted = _load!(manager, namePtr, 0) != 0; }
            finally { _healing = false; }
            nint healed = CallOriginal(manager, key);
            if (healed != 0) { _healed++; _attempts.Remove((manager, name)); } else _refused++;
            TraceHeal(manager, name, accepted, healed != 0);
            if (healed == 0 && IconCacheHealPolicy.AttemptsExhausted(tries))
                ModLogger.Warn(LogVerb.Display, $"IconCacheHeal gave up on {IconCacheHealPolicy.Tail(name)} for cache 0x{(long)manager:X} after {tries} tries; it shows old art until a relaunch.");
            return healed;
        }
    }

    /// <summary>Reads the key's name pointer, then the name in two steps so one beside a page edge still reads.</summary>
    private string? ReadName(nint key, out nint namePtr)
    {
        namePtr = 0;
        if (!_mem.TryReadBytes(key, 8, out var raw) || raw.Length < 8) return null;
        namePtr = (nint)BitConverter.ToInt64(raw, 0);
        if (namePtr == 0) return null;
        int firstLen = IconCacheHealPolicy.FirstReadLength(namePtr);
        if (!_mem.TryReadBytes(namePtr, firstLen, out var first)) return null;
        byte[]? second = null;
        if (Array.IndexOf(first, (byte)0) < 0 && firstLen < IconCacheHealPolicy.MaxNameBytes
            && !_mem.TryReadBytes(namePtr + firstLen, IconCacheHealPolicy.MaxNameBytes - firstLen, out second))
            return null;
        return IconCacheHealPolicy.NameFrom(first, second);
    }

#if !LWDEV
    internal nint Detour(nint manager, nint key) => DetourBody(manager, key);
    private nint CallOriginal(nint manager, nint key) => _original!(manager, key);
    private void InitTrace(string? modDir) { }
    private static void TraceHeal(nint manager, string name, bool accepted, bool healed) { }
#endif
}
