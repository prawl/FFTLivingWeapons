using System;
using System.Collections.Generic;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Native half of the save edges (untestable, like PromptSwapHook). Three
/// Reloaded.Hooks detours behind prologue landmarks. The two load detours forward to the
/// original, then read the save struct header through the guarded patcher and hand it to
/// <see cref="SaveEdgeTracker"/>. The serialize detour first projects the three
/// relocated chart blocks (<see cref="TemplateSync.Project"/>), then forwards and reads the
/// header. Detours never throw and log only the once-per-session canaries.
///
/// The load detours also write game memory (the serialize detour too, for the projection).
/// The routine they wrap copies the save's bag over the count array (the file carries ids
/// 0..260, so extended ids land on zero) and restores both menu order templates from the save
/// struct (0x14021B4BD / 0x14021B5BB). Nothing in the load path rebuilds them from item data
/// (re-test 4 and 5, closing disassembly 2026-08-30). So after the original returns, the detour
/// replays the bag counts (BagReplay) and reseats a few template words (TemplateSeat), all
/// through the guarded patcher. Engine's tick runs the same replay as an idempotent fallback
/// and keeps the log line; its seat runs only when this detour did not serve the load edge
/// (round 8c).
///
/// Landmarks (re-read from the 1.5.2 exe on disk 2026-08-27, after the first build named two
/// entries that were not the routines it meant):
///   save-serialize 0x140218F78 48 89 5C 24 08 48 89 6C 24 20 56 57 41 54 41 56
///   load-apply     0x14021B0E8 48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48
///   load-restore-b 0x14021DE98 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57
/// Every save caller reaches the serializer through the wrapper 0x14021B070's tail jump, so one
/// entry covers manual saves and autosave. The load-apply sits at the byte after that jump. The
/// async file-op stepper 0x14021DDF0 is not hooked. It runs twice a frame during a save or a
/// load, so a load edge taken from it would fire on saves too. The struct pointer normally holds
/// the static image buffer 0x142C81C80 (Offsets.SaveStructStatic), so the header read after the
/// original returns is the struct just filled or applied.
/// Signatures are unknown (Microsoft x64, up to four args), so the detours forward rcx/rdx/r8/r9
/// and return rax, as PromptSwapHook does.
///
/// The detour bodies and AfterApply are in SaveEdgeHooks.Detours.cs (200-line limit).
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
    private readonly Action<string>? _replayOnLoad;   // the bag replay, by save key
    private readonly long _bagBase;   // ExtendedInventory.BagCountBase, set at construction
    private readonly TemplateRelocation? _templates;   // null = no template sync (every pre-existing caller)
    private IHook<EdgeFn>? _serialize, _apply, _applyB;
    private EdgeFn? _k1, _k2, _k3;   // GC anchors. The native thunks must outlive us
    private bool _canarySave, _canaryLoadA, _canaryLoadB;

    public bool IsActive => _serialize?.IsHookEnabled == true && _apply?.IsHookEnabled == true && _applyB?.IsHookEnabled == true;

    /// <param name="bagBase">Where the bag bytes live (<see
    /// cref="ExtendedInventory.BagCountBase"/> in production); defaults to the vanilla block.</param>
    /// <param name="templates">for <see cref="TemplateSync"/> project/adopt. Null makes
    /// both edges no-ops.</param>
    public SaveEdgeHooks(ICodePatcher mem, SaveEdgeTracker tracker, IReadOnlyList<int> extendedIds,
        Action<string>? replayOnLoad = null, long bagBase = 0, TemplateRelocation? templates = null)
    {
        _mem = mem;
        _tracker = tracker;
        _extendedIds = extendedIds;
        _replayOnLoad = replayOnLoad;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;   // 0 = the vanilla block (a rebased pin cannot be a default value)
        _templates = templates;
    }

    /// <summary>Pure. Install only when the guarded read succeeded and the prologue matches.</summary>
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

    /// <summary>Internal as a test seam, like <see cref="AfterApply"/>. Reads through
    /// <see cref="_bagBase"/>.</summary>
    internal Dictionary<int, int> ReadCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (int id in _extendedIds)
            counts[id] = _mem.TryRead(_bagBase + id, 1, out var b) ? b[0] : 0;
        return counts;
    }

}
