using System;
using System.Collections.Generic;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// LW-353: the native half of the save edges (untestable by construction, the PromptSwapHook
/// posture): three Reloaded.Hooks detours behind prologue landmarks, each forwarding to the
/// original FIRST and then, once the struct is complete (serialize) or applied (load), reading
/// the save struct's header through the guarded patcher and handing it to
/// <see cref="SaveEdgeTracker"/>. A detour never throws, never logs on the hot path beyond the
/// once-per-session canaries, and never writes game memory; the replay itself lands on the tick.
///
/// Landmarks (read on the 1.5.2 exe on disk 2026-08-27, each preceded by ret/CC padding):
///   serializer   0x140218F78: 48 89 5C 24 08 48 89 6C 24 20 56 57 41 54 41 56
///   load-apply   0x14021B070: 48 89 5C 24 08 57 44 0F B6 1D
///   load-apply B 0x14021DDF0: 48 83 EC 28 8B 15 E2 1C C6 02 85 D2
/// Signatures are unknown beyond "Microsoft x64, up to four register args"; the detours forward
/// rcx/rdx/r8/r9 verbatim and return the original's rax, the same contract PromptSwapHook uses.
/// </summary>
internal sealed class SaveEdgeHooks
{
    [Function(CallingConventions.Microsoft)]
    public delegate nint EdgeFn(nint rcx, nint rdx, nint r8, nint r9);

    public static readonly byte[] SerializePrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x20, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56 };
    public static readonly byte[] ApplyPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x44, 0x0F, 0xB6, 0x1D };
    public static readonly byte[] ApplyBPrologue = { 0x48, 0x83, 0xEC, 0x28, 0x8B, 0x15, 0xE2, 0x1C, 0xC6, 0x02, 0x85, 0xD2 };

    private readonly ICodePatcher _mem;
    private readonly SaveEdgeTracker _tracker;
    private readonly IReadOnlyList<int> _extendedIds;
    private IHook<EdgeFn>? _serialize, _apply, _applyB;
    private EdgeFn? _k1, _k2, _k3;   // GC anchors: the native thunks must outlive us
    private bool _canarySave, _canaryLoad;

    public bool IsActive => _serialize?.IsHookEnabled == true && _apply?.IsHookEnabled == true && _applyB?.IsHookEnabled == true;

    public SaveEdgeHooks(ICodePatcher mem, SaveEdgeTracker tracker, IReadOnlyList<int> extendedIds)
    {
        _mem = mem;
        _tracker = tracker;
        _extendedIds = extendedIds;
    }

    /// <summary>Pure: install only when the guarded read succeeded and the prologue matches.</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue, byte[] expected) => readOk && HookLandmark.Verify(prologue, expected);

    /// <summary>Null on success, else the refusal. All three land or none does.</summary>
    public string? Install(IReloadedHooks hooks)
    {
        if (_serialize != null) return null;
        foreach (var (addr, expected, label) in new[]
        {
            (Offsets.FnSaveSerialize, SerializePrologue, "save-serialize"),
            (Offsets.FnSaveApply, ApplyPrologue, "save-apply"),
            (Offsets.FnSaveApplyB, ApplyBPrologue, "save-apply-b"),
        })
        {
            bool ok = _mem.TryRead(addr, expected.Length, out var entry);
            if (!ShouldArm(ok, entry, expected))
                return $"{label}: 0x{addr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        }
        try
        {
            _k1 = DetourSerialize; _k2 = DetourApply; _k3 = DetourApplyB;
            _serialize = hooks.CreateHook<EdgeFn>(_k1, Offsets.FnSaveSerialize).Activate();
            _apply = hooks.CreateHook<EdgeFn>(_k2, Offsets.FnSaveApply).Activate();
            _applyB = hooks.CreateHook<EdgeFn>(_k3, Offsets.FnSaveApplyB).Activate();
            return null;
        }
        catch (Exception ex)
        {
            Release();
            return $"save-edge hooks: install failed ({ex.Message})";
        }
    }

    public void Release()
    {
        foreach (var h in new[] { _serialize, _apply, _applyB })
        {
            try { if (h?.IsHookEnabled == true) h.Disable(); } catch (Exception) { /* release must never throw */ }
        }
    }

    /// <summary>The 0xB8 header bytes of the struct the global points at, or null when the
    /// pointer is null or the read fails.</summary>
    internal byte[]? ReadHeader()
    {
        if (!_mem.TryRead(Offsets.SaveStructPtr, 8, out var p)) return null;
        long ptr = BitConverter.ToInt64(p, 0);
        if (ptr <= 0) return null;
        return _mem.TryRead(ptr + Offsets.SaveHeaderKeyOff, Offsets.SaveHeaderKeyLen, out var hdr) ? hdr : null;
    }

    private Dictionary<int, int> ReadCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (int id in _extendedIds)
            counts[id] = _mem.TryRead(Offsets.BagCountArray + id, 1, out var b) ? b[0] : 0;
        return counts;
    }

    private nint DetourSerialize(nint rcx, nint rdx, nint r8, nint r9)
    {
        nint ret = _serialize!.OriginalFunction(rcx, rdx, r8, r9);
        try
        {
            var hdr = ReadHeader();
            if (hdr != null)
            {
                _tracker.OnSerialized(hdr, ReadCounts());
                if (!_canarySave) { _canarySave = true; SafeLog("The save-edge hook is confirmed working; the game's first save this session was intercepted."); }
            }
        }
        catch (Exception) { /* a managed fault here must never reach the game */ }
        return ret;
    }

    private nint DetourApply(nint rcx, nint rdx, nint r8, nint r9)
    {
        nint ret = _apply!.OriginalFunction(rcx, rdx, r8, r9);
        AfterApply();
        return ret;
    }

    private nint DetourApplyB(nint rcx, nint rdx, nint r8, nint r9)
    {
        nint ret = _applyB!.OriginalFunction(rcx, rdx, r8, r9);
        AfterApply();
        return ret;
    }

    private void AfterApply()
    {
        try
        {
            var hdr = ReadHeader();
            if (hdr == null) return;   // not a save load (the struct pointer is null): nothing to key
            _tracker.OnApplied(hdr);
            if (!_canaryLoad) { _canaryLoad = true; SafeLog("The load-edge hook is confirmed working; the game's first save load this session was intercepted."); }
        }
        catch (Exception) { /* never reaches the game */ }
    }

    private static void SafeLog(string line)
    {
        try { ModLogger.Event(LogVerb.Save, line); } catch (Exception) { }
    }
}
