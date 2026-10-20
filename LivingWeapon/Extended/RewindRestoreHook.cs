using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Detours on the two routines that rewind a battle. The flee restore (<see cref="Offsets.FnSnapshotRestore"/>)
/// copies the pre battle snapshot over the live roster and the vanilla bag, and the checkpoint copy
/// (<see cref="Offsets.FnCheckpointCopy"/>, mode 1) does the same for a Retry. Both stop at bag id 260, so a new
/// weapon equipped on the formation screen comes back out of the bag and never returns.
/// The flee detour hands the snapshot header to the load edge path. The checkpoint detour keeps the extended totals
/// owned (bag plus roster hands) at mode 0 and sets the bag to total minus hands after mode 1. Signatures are unknown, so four registers are forwarded.
/// </summary>
internal sealed class RewindRestoreHook
{
    // Nobody knows what the game passes these routines, so all four argument slots go straight back untouched.
    [Function(CallingConventions.Microsoft)]
    public delegate nint RewindFn(nint rcx, nint rdx, nint r8, nint r9);

    // The first bytes of each routine work like a fingerprint. If a game patch moves the code they stop matching and the hook stays off instead of landing in the wrong place.
    public static readonly byte[] ExpectedRestorePrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57, 0x48, 0x83, 0xEC, 0x30, 0xBA, 0x01, 0x00, 0x00, 0x00, 0xE8 };
    // This tail points rdi at the bag, 0x7E90 bytes into the checkpoint struct.
    internal static readonly byte[] CheckpointLeaRdiBagTail = { 0x48, 0x83, 0xEC, 0x20, 0x48, 0x8D, 0xB9, 0x90, 0x7E, 0x00, 0x00 };
    internal static readonly byte[] StockFourRegisterFrame = SaveEdgeHooks.ApplyBPrologue;
    // The compiler opens many routines with this frame, so only the lea tail that follows marks the checkpoint copy.
    public static readonly byte[] ExpectedCheckpointPrologue = StockFourRegisterFrame.Concat(CheckpointLeaRdiBagTail).ToArray();

    private readonly ICodePatcher _mem;
    private readonly Action<byte[]> _onRewind;
    private readonly long _restoreAddr, _checkpointAddr;
    private readonly RewindTotals _totals;
    private readonly CheckpointTotalShadow _totalsByStruct = new();
    private readonly object _gate = new();
    private IHook<RewindFn>? _restoreHook, _checkpointHook;
    private RewindFn? _k1, _k2;   // GC anchors, the native thunks must outlive us
    private long _rewindsHandedOn, _unread, _saves, _savesIncomplete, _restores, _unrecorded, _restoresSeen;
    // Each warning prints once per launch so one bad read cannot flood the log.
    private bool _warnedUnrecorded, _warnedUnread;

    public bool IsActive => _restoreHook?.IsHookEnabled == true && _checkpointHook?.IsHookEnabled == true;
    public long RewindsHandedOn => Interlocked.Read(ref _rewindsHandedOn);
    public long RewindsUnread => Interlocked.Read(ref _unread);
    public long CheckpointSaves => Interlocked.Read(ref _saves);
    public long CheckpointSavesIncomplete => Interlocked.Read(ref _savesIncomplete);
    public long CheckpointRestores => Interlocked.Read(ref _restores);
    public long CheckpointRestoresSeen => Interlocked.Read(ref _restoresSeen);
    public long CheckpointRestoresUnrecorded => Interlocked.Read(ref _unrecorded);
    internal CheckpointTotalShadow TotalsByStruct => _totalsByStruct;

    public RewindRestoreHook(ICodePatcher mem, Action<byte[]> onRewind, RewindTotals totals,
        long restoreAddr = 0, long checkpointAddr = 0)
    {
        _mem = mem;
        _totals = totals;
        _onRewind = onRewind;
        _restoreAddr = restoreAddr == 0 ? Offsets.FnSnapshotRestore : restoreAddr;   // a rebased pin cannot be a default value
        _checkpointAddr = checkpointAddr == 0 ? Offsets.FnCheckpointCopy : checkpointAddr;
    }

    internal static bool ShouldArm(bool readOk, byte[]? prologue, byte[] expected) => readOk && HookLandmark.Verify(prologue, expected);

    /// <summary>Null on success, else the refusal. Idempotent, and a refusal leaves nothing active.</summary>
    public string? Install(IReloadedHooks hooks)
    {
        // Installing again after a release only switches the same hooks back on and never stacks a second detour.
        if (_restoreHook != null) { if (!IsActive) { _restoreHook.Enable(); _checkpointHook?.Enable(); } return null; }
        // Both fingerprints are checked before either hook goes in, so a refusal never leaves one half installed.
        foreach (var (addr, expected, label) in new[]
        {
            (_restoreAddr, ExpectedRestorePrologue, "rewind-restore"),
            (_checkpointAddr, ExpectedCheckpointPrologue, "checkpoint-copy"),
        })
        {
            bool ok = _mem.TryRead(addr, expected.Length, out var entry);
            if (!ShouldArm(ok, entry, expected))
                return $"{label}: 0x{addr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        }
        try
        {
            _k1 = DetourRestore; _k2 = DetourCheckpoint;
            _restoreHook = hooks.CreateHook<RewindFn>(_k1, _restoreAddr).Activate();
            _checkpointHook = hooks.CreateHook<RewindFn>(_k2, _checkpointAddr).Activate();
            return null;
        }
        catch (Exception ex)
        {
            Release();
            _restoreHook = null; _checkpointHook = null;
            return $"rewind-restore: hook install failed ({ex.Message})";
        }
    }

    public void Release()
    {
        foreach (var h in new[] { _restoreHook, _checkpointHook })
        {
            try { if (h?.IsHookEnabled == true) h.Disable(); }
            catch (Exception ex) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, "rewind-restore release swallowed: " + ex.Message)); }
        }
    }

    private nint DetourRestore(nint rcx, nint rdx, nint r8, nint r9) => ProcessRewind(_restoreHook!.OriginalFunction, rcx, rdx, r8, r9);

    private nint DetourCheckpoint(nint rcx, nint rdx, nint r8, nint r9) => ProcessCheckpoint(_checkpointHook!.OriginalFunction, rcx, rdx, r8, r9);

    /// <summary>Runs the flee restore, then hands the snapshot's key header to the load edge path.</summary>
    internal nint ProcessRewind(RewindFn original, nint rcx, nint rdx, nint r8, nint r9)
    {
        nint result = original(rcx, rdx, r8, r9);   // once, outside any try
        // The game's own rewind always runs first and exactly once, so a bug below can never stop it.
        try
        {
            // The header names the save this snapshot came from, and the load edge path looks up that save's own counts by it.
            bool ok = _mem.TryRead(Offsets.SnapshotStructCopy + Offsets.SaveHeaderKeyOff, Offsets.SaveHeaderKeyLen, out var header);
            if (ok) { Interlocked.Increment(ref _rewindsHandedOn); _onRewind(header); }
            else NoteUnread();
        }
        catch (Exception) { /* the game's own state stands */ }
        return result;
    }

    /// <summary>Mode 0 records the extended totals under the struct, mode 1 re derives the bag from them once the original has run.</summary>
    internal nint ProcessCheckpoint(RewindFn original, nint rcx, nint rdx, nint r8, nint r9)
    {
        // Mode 0 is the game saving its Retry point. Mode 1 is a Retry putting it back.
        var mode = RewindPolicy.ModeOf(unchecked((int)rdx));
        nint result = original(rcx, rdx, r8, r9);   // once, outside any try
        try
        {
            if (mode == RewindMode.Store) SaveCounts((long)rcx);
            else if (mode == RewindMode.Restore)
            {
                Interlocked.Increment(ref _restoresSeen);
                RestoreCounts((long)rcx);
            }
        }
        catch (Exception) { /* the game's own state stands */ }
        return result;
    }

    private void NoteUnread()
    {
        Interlocked.Increment(ref _unread);
        SafeLog(() => Flight.Record("rewind-restore", "header=unreadable"));
        if (_warnedUnread) return;
        _warnedUnread = true;
        SafeLog(() => ModLogger.Warn(LogVerb.Save, "The battle was rewound but the pre battle save's header could not be read, so the new weapons' bag counts were left as they are."));
    }

    // The total owned is kept, never the bag alone. A Retry snaps every unit back to its old gear, so the right bag is whatever the hands do not hold.
    private void SaveCounts(long structAddr)
    {
        var captured = _totals.Capture("checkpoint") ? _totals.Latest : null;
        if (captured == null) { Interlocked.Increment(ref _savesIncomplete); return; }
        lock (_gate) _totalsByStruct.RememberForStruct(structAddr, captured);
        Interlocked.Increment(ref _saves);
    }

    private void RestoreCounts(long structAddr)
    {
        IReadOnlyDictionary<int, int> totals;
        bool found;
        lock (_gate) found = _totalsByStruct.TryGetForStruct(structAddr, out totals!);
        // No record means the Retry point was saved before this launch, most likely inside the save file, so the bag is left alone.
        if (!found)
        {
            Interlocked.Increment(ref _unrecorded);
            SafeLog(() => Flight.Record("checkpoint-restore", $"struct=0x{structAddr:X} record=missing"));
            if (_warnedUnrecorded) return;
            _warnedUnrecorded = true;
            SafeLog(() => ModLogger.Warn(LogVerb.Save, "A battle checkpoint was put back that this mod never saw being saved, so the new weapons' bag counts may be off until the next save loads."));
            return;
        }
        var wrote = _totals.PutBack(totals, "checkpoint restore");
        Interlocked.Increment(ref _restores);
        string describe = RewindPolicy.Describe(wrote);
        SafeLog(() => Flight.Record("checkpoint-restore", $"struct=0x{structAddr:X} counts={describe}"));
        SafeLog(() => ModLogger.Event(LogVerb.Save, $"A retried battle put its checkpoint back; the new weapons' bag counts were put back with it ({describe})."));
    }

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
