using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Stops the new items from reading x0 after every battle.
/// The game's inventory reset (<see cref="Offsets.FnInventoryReset"/>, 0x140284500) wipes the owned counts
/// and rebuilds them from the save, but only for the base game's 261 items, so ours came back as zero.
/// The hook saves them before the reset and writes them back after, and only when the reset actually wiped them.
/// The wipe reaches our ids because the loop bound at 0x140284554 was widened (0x105 to 0x105 + N) so the
/// per item state arrays cover them, and the loop's first pass zeroes the bag
/// (byte [0x1411A7C00 + id] = 0, at 0x140284561).
/// Read from the 1.5.2 exe with capstone. The 12 byte prologue at 140284500 is
/// 48 89 5c 24 08 / 56 / 48 83 ec 20 / 8b d9 (mov [rsp+8],rbx, push rsi, sub rsp,0x20, mov ebx,ecx),
/// and ecx is the mode. It returns at 0x14028467C, and 0x14028467D is its range check abort.
/// Nothing calls it directly, so the first log line is how we know it ran. Nobody proved eax goes
/// unread, so rax is passed back as the game left it.
/// The second byte array 0x1411A7700 (per item save state at struct +0x84AD) is not put back, and it has
/// stayed zero for our ids in every live pass. The restore also ignores the mode, so if New Game runs
/// through here the last save's counts come back until the next load replaces them (the log line names the mode).
/// </summary>
internal sealed class InventoryResetHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate nint ResetFn(int mode);

    /// <summary>The 12 bytes the class summary decodes.</summary>
    public static readonly byte[] ExpectedPrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x8B, 0xD9 };

    private readonly ICodePatcher _mem;
    private readonly int _count;
    private readonly long _bagBase;
    private IHook<ResetFn>? _hook;
    private ResetFn? _keepalive;   // keeps the delegate alive as long as the native thunk points at it
    private long _runs;
    private long _restores;
    private bool _canary;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    /// <summary>How many times the game's reset ran through the detour.</summary>
    public long Runs => Interlocked.Read(ref _runs);
    /// <summary>How many of those runs wiped our counts and had them put back.</summary>
    public long Restores => Interlocked.Read(ref _restores);

    /// <param name="extendedCount">How many new items exist, ids 261 up to 261+N-1.</param>
    /// <param name="bagBase">Where the bag counts live, <see
    /// cref="ExtendedInventory.BagCountBase"/> in production and the game's own block when left at 0.</param>
    public InventoryResetHook(ICodePatcher mem, int extendedCount, long targetAddr = 0, long bagBase = 0)
    {
        _mem = mem;
        _count = extendedCount;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;   // a rebased address cannot be a default parameter
        TargetAddr = targetAddr == 0 ? Offsets.FnInventoryReset : targetAddr;
    }

    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else why not. Safe to call twice.</summary>
    public string? Install(IReloadedHooks hooks)
    {
        if (_hook != null) { if (!_hook.IsHookEnabled) _hook.Enable(); return null; }
        bool ok = _mem.TryRead(TargetAddr, ExpectedPrologue.Length, out var entry);
        if (!ShouldArm(ok, entry))
            return $"inventory-reset: 0x{TargetAddr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<ResetFn>(_keepalive, TargetAddr).Activate();
            return null;
        }
        catch (Exception ex) { return $"inventory-reset: hook install failed ({ex.Message})"; }
    }

    public void Release()
    {
        try { if (_hook?.IsHookEnabled == true) _hook.Disable(); }
        catch (Exception ex) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, "inventory-reset release swallowed: " + ex.Message)); }
    }

    /// <summary>The detour body, with the game's routine passed in so tests can stand in for it.
    /// If the bag cannot be read beforehand there is nothing to put back, and the reset runs as normal.</summary>
    internal nint Process(int mode, ResetFn original)
    {
        Interlocked.Increment(ref _runs);
        long addr = _bagBase + ExtendedCatalog.FirstExtendedId;
        byte[]? before = _count > 0 && _mem.TryRead(addr, _count, out var b) ? b : null;
        nint result = original(mode);   // once, and outside any try so a game exception reaches the game
        try { Restore(addr, before, mode); }
        catch (Exception) { /* a throw here leaves the game's own reset as it is */ }
        return result;
    }

    private void Restore(long addr, byte[]? before, int mode)
    {
        if (before == null) return;
        if (!_mem.TryRead(addr, _count, out var after)) return;
        // Not every reset reaches our bytes, and one that left them alone writes and logs nothing.
        if (Same(before, after)) return;
        if (!_mem.TryWrite(addr, before))
        {
            SafeLog(() => ModLogger.Warn(LogVerb.Save, "The game reset its inventory table and the extended counts could not be put back (write refused); the new items may read as owned x0 until the next save loads."));
            return;
        }
        Interlocked.Increment(ref _restores);
        int kept = 0;
        foreach (var v in before) if (v != 0) kept++;
        string line = $"The game reset its inventory table (mode {mode}); {kept} extended count(s) kept.";
        Flight.Record("inventory-reset", $"mode={mode} kept={kept} of {_count}");
        if (!_canary) { _canary = true; SafeLog(() => ModLogger.Event(LogVerb.Save, line)); }
        else SafeLog(() => ModLogger.Debug(LogVerb.Save, line));
    }

    private static bool Same(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // Runs inside the game, so nothing past the game's own call may throw.
    private nint Detour(int mode) => Process(mode, _hook!.OriginalFunction);

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
