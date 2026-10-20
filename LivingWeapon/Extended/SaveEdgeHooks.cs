using System;
using System.Collections.Generic;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Catches the moment the game saves or loads, so the new items' counts can ride along in our own file.
/// Three detours, each behind a prologue check. The two load detours call the game first, then read the
/// save header and hand it to <see cref="SaveEdgeTracker"/>. The save detour first copies our three
/// menu order charts back into the game's own (<see cref="TemplateSync.Project"/>) so the save file
/// holds them, then calls the game and reads the header. No detour throws, and each logs only its
/// first sighting per session. Untestable natively, like PromptSwapHook.
///
/// A load wipes the new items. The game copies the save's bag over the count array, and the file only
/// holds ids 0 to 260, so ours land on zero. It also restores both menu order charts from the save
/// struct (0x14021B4BD and 0x14021B5BB), and nothing on the load path rebuilds them from item data
/// (re-tests 4 and 5 and the disassembly). So once the game returns, the detour hands the
/// load to the replay callback, which puts the counts back and reseats a few chart words through the guarded patcher.
/// Engine's tick runs the same replay again as a harmless fallback and writes the log line, and it
/// only reseats when this detour missed the load.
///
/// Prologues, read from the 1.5.2 exe on disk [journal 2026-08-27].
///   save-serialize 0x140218F78 48 89 5C 24 08 48 89 6C 24 20 56 57 41 54 41 56
///   load-apply     0x14021B0E8 48 8B C4 48 89 58 08 48 89 68 10 48 89 70 18 48
///   load-restore-b 0x14021DE98 48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57
/// Manual saves and autosave both reach the serializer through the tail jump of 0x14021B070, so one
/// hook catches both, and load-apply starts on the byte after that jump. The file stepper 0x14021DDF0
/// is left alone because it runs twice a frame during saves and loads alike, so it cannot tell them
/// apart. The struct pointer normally holds the static buffer 0x142C81C80 (Offsets.SaveStructStatic),
/// so the header read after the game returns is the struct it just filled or applied.
/// Nobody knows these routines' arguments (Microsoft x64, up to four), so rcx, rdx, r8 and r9 pass
/// straight through and rax comes back, as in PromptSwapHook.
///
/// The detour bodies and AfterApply are in SaveEdgeHooks.Detours.cs.
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
    private readonly Action<string>? _replayOnLoad;   // puts the bag counts back for a save key
    private readonly long _bagBase;
    private readonly Func<string, bool> _hasRecord;
    private readonly Action? _onSerialized, _onRewound, _onApplied;   // called after a save, a rewind or a load so the caller can capture the total owned, null when it keeps none
    private readonly TemplateRelocation? _templates;   // null skips the chart copy both ways
    private IHook<EdgeFn>? _serialize, _apply, _applyB;
    private EdgeFn? _k1, _k2, _k3;   // keeps the delegates alive as long as the native thunks point at them
    private bool _canarySave, _canaryLoadA, _canaryLoadB;

    public bool IsActive => _serialize?.IsHookEnabled == true && _apply?.IsHookEnabled == true && _applyB?.IsHookEnabled == true;

    /// <param name="bagBase">Where the bag counts live, <see
    /// cref="ExtendedInventory.BagCountBase"/> in production and the game's own block when left at 0.</param>
    /// <param name="templates">Null turns off the <see cref="TemplateSync"/> copy on both edges.</param>
    public SaveEdgeHooks(ICodePatcher mem, SaveEdgeTracker tracker, IReadOnlyList<int> extendedIds,
        Action<string>? replayOnLoad = null, long bagBase = 0, TemplateRelocation? templates = null,
        Func<string, bool>? hasRecord = null, Action? onSerialized = null, Action? onRewound = null, Action? onApplied = null)
    {
        _mem = mem;
        _tracker = tracker;
        _extendedIds = extendedIds;
        _replayOnLoad = replayOnLoad;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;   // a rebased address cannot be a default parameter
        _templates = templates;
        _hasRecord = hasRecord ?? (_ => true);
        _onSerialized = onSerialized;
        _onRewound = onRewound;
        _onApplied = onApplied;
    }

    internal static bool ShouldArm(bool readOk, byte[]? prologue, byte[] expected) => readOk && HookLandmark.Verify(prologue, expected);

    /// <summary>Null on success, else why not. All three prologues are checked before any hook goes in.</summary>
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
            try { if (h?.IsHookEnabled == true) h.Disable(); } catch (Exception) { /* a release that throws would take the rollback down with it */ }
        }
    }

    /// <summary>The 0xB8 header bytes of the save struct, or null when the pointer is empty or a read fails.</summary>
    internal byte[]? ReadHeader()
    {
        if (!_mem.TryRead(Offsets.SaveStructPtr, 8, out var p)) return null;
        long ptr = BitConverter.ToInt64(p, 0);
        if (ptr <= 0) return null;
        return _mem.TryRead(ptr + Offsets.SaveHeaderKeyOff, Offsets.SaveHeaderKeyLen, out var hdr) ? hdr : null;
    }

    /// <summary>Internal so tests can call it, like <see cref="AfterApply"/>.</summary>
    internal Dictionary<int, int> ReadCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (int id in _extendedIds)
            counts[id] = _mem.TryRead(_bagBase + id, 1, out var b) ? b[0] : 0;
        return counts;
    }

}
