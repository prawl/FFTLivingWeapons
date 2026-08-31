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
/// <see cref="SaveEdgeTracker"/>. A detour never throws and never logs on the hot path beyond the
/// once-per-session canaries.
///
/// LW-351 CHANGES ONE POSTURE ON PURPOSE: the load detours DO write game memory, the only place
/// these hooks ever do, and they write in the one moment nothing else can. The routine they wrap
/// undoes two things the extended items need on its way past: it copies the save file's bag over
/// the game's count array (that file carries ids 0..260, so every extended id lands on zero) and
/// it RESTORES both menu order templates byte-for-byte out of the save struct (0x14021B4BD /
/// 0x14021B5BB), which for any save written before a new id ever seated means a table that will
/// never name it. Round 4 (re-test 4) proved the first half, round 5 (re-test 5 plus the closing
/// disassembly, 2026-08-30) proved the second: nothing in the load path rebuilds those templates
/// from the item data, so an owned-but-unworn new item could never be listed or equipped. Both
/// repairs therefore happen right here, after the original returns: N bounded one-byte writes into
/// the bag-count array (BagReplay) plus at most a few words into each order template
/// (TemplateSeat), all through the guarded patcher, and nothing else. Engine's tick still runs the
/// same replay afterwards as an idempotent fallback and keeps the one log line (BagReplay holds
/// the resolution both paths share); the seat it runs only when this detour did not serve the
/// load edge (round 8c), and the detour's repair and refusal notes reach the log through it.
///
/// Landmarks (re-read from the 1.5.2 exe on disk 2026-08-27 late night, after the first build
/// named two entries that were not the routines it meant):
///   save-serialize 0x140218F78: 48 89 5C 24 08 48 89 6C 24 20 56 57 41 54 41 56
///   load-apply     0x14021B0E8: 48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48
///   load-restore-b 0x14021DE98: 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57
/// Every save caller reaches the serializer through the wrapper 0x14021B070's tail jump, so that
/// one entry covers manual saves and the autosave. The load-apply sits at the byte right after
/// that jump. The async file-op stepper 0x14021DDF0 is deliberately NOT hooked: it is called
/// twice a frame while a save OR a load is in flight, so a load edge taken from it would fire on
/// saves too. The struct pointer normally holds the static image buffer 0x142C81C80
/// (Offsets.SaveStructStatic), so the header read after the original returns is the struct that
/// routine just filled or applied.
/// Signatures are unknown beyond "Microsoft x64, up to four register args"; the detours forward
/// rcx/rdx/r8/r9 verbatim and return the original's rax, the same contract PromptSwapHook uses.
///
/// This file is construction, Install/Release and the two read helpers (ReadHeader/ReadCounts);
/// the three detour bodies and AfterApply are split into SaveEdgeHooks.Detours.cs under the
/// 200-line house guideline (the same lifecycle/hot-path seam ExtendedInventory.cs and
/// ExtendedInventory.Hooks.cs already use).
/// </summary>
internal sealed partial class SaveEdgeHooks
{
    [Function(CallingConventions.Microsoft)]
    public delegate nint EdgeFn(nint rcx, nint rdx, nint r8, nint r9);

    public static readonly byte[] SerializePrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x20, 0x56, 0x57, 0x41, 0x54, 0x41, 0x56 };
    public static readonly byte[] ApplyPrologue = { 0x48, 0x8B, 0xC4, 0x48, 0x89, 0x58, 0x08, 0x48, 0x89, 0x68, 0x10, 0x48, 0x89, 0x70, 0x18, 0x48 };
    public static readonly byte[] ApplyBPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x10, 0x48, 0x89, 0x74, 0x24, 0x18, 0x57 };

    private readonly ICodePatcher _mem;
    private readonly SaveEdgeTracker _tracker;
    private readonly IReadOnlyList<int> _extendedIds;
    private readonly Action<string>? _replayOnLoad;   // LW-351: the bag replay, by save key
    private readonly long _bagBase;   // LW-368 round 2: ExtendedInventory.BagCountBase, set at construction
    private IHook<EdgeFn>? _serialize, _apply, _applyB;
    private EdgeFn? _k1, _k2, _k3;   // GC anchors: the native thunks must outlive us
    private bool _canarySave, _canaryLoadA, _canaryLoadB;

    public bool IsActive => _serialize?.IsHookEnabled == true && _apply?.IsHookEnabled == true && _applyB?.IsHookEnabled == true;

    /// <param name="bagBase">LW-368 round 2: where the bag bytes live -- <see
    /// cref="ExtendedInventory.BagCountBase"/> in production (the relocated page once armed,
    /// else the vanilla block); defaults to the vanilla block so every pre-existing caller keeps
    /// compiling unchanged.</param>
    public SaveEdgeHooks(ICodePatcher mem, SaveEdgeTracker tracker, IReadOnlyList<int> extendedIds,
        Action<string>? replayOnLoad = null, long bagBase = Offsets.BagCountArray)
    {
        _mem = mem;
        _tracker = tracker;
        _extendedIds = extendedIds;
        _replayOnLoad = replayOnLoad;
        _bagBase = bagBase;
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
            (Offsets.FnSaveApply, ApplyPrologue, "load-apply"),
            (Offsets.FnSaveApplyB, ApplyBPrologue, "load-restore-b"),
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
    /// pointer does not resolve or the read fails.</summary>
    internal byte[]? ReadHeader()
    {
        if (!_mem.TryRead(Offsets.SaveStructPtr, 8, out var p)) return null;
        long ptr = BitConverter.ToInt64(p, 0);
        if (ptr <= 0) return null;
        return _mem.TryRead(ptr + Offsets.SaveHeaderKeyOff, Offsets.SaveHeaderKeyLen, out var hdr) ? hdr : null;
    }

    /// <summary>Internal (not private): a test seam, the same idiom <see cref="AfterApply"/>
    /// already uses to bypass the native trampoline. Reads through <see cref="_bagBase"/>
    /// (LW-368 round 2).</summary>
    internal Dictionary<int, int> ReadCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (int id in _extendedIds)
            counts[id] = _mem.TryRead(_bagBase + id, 1, out var b) ? b[0] : 0;
        return counts;
    }

}
