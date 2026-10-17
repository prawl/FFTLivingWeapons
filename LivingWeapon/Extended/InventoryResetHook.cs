using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Detour on the game's per-item inventory reset
/// (<see cref="Offsets.FnInventoryReset"/>, 0x140284500). The routine wipes the owned-count table and
/// rebuilds it from the save, but only for the base game's 261 items, so extended counts read x0 after
/// every real battle (seen 2026-08-30 23:29, no load edge in between). This hook saves them before the
/// reset and writes them back after.
/// The wipe comes from fix round 2, which widened the loop bound (0x140284554, 0x105 -&gt; 0x105 + N) so
/// the per-item state arrays cover the new ids. The loop's first pass zeroes the bag array
/// (byte [0x1411A7C00 + id] = 0, at 0x140284561), which now includes 261+.
/// Read from the 1.5.2 exe with capstone on 2026-08-30. The 12-byte prologue at 140284500 is
/// 48 89 5c 24 08 / 56 / 48 83 ec 20 / 8b d9 (mov [rsp+8],rbx; push rsi; sub rsp,0x20; mov ebx,ecx),
/// and ecx is the mode. The routine ends with ret at 0x14028467C; 0x14028467D is the range-check abort.
/// No direct caller exists (indirect dispatch), so the canary log line shows when it runs. Nothing
/// proved eax unread, so the delegate returns nint and the detour passes rax through.
/// Not restored. The second byte array 0x1411A7700 (per-item save state at struct +0x84AD), which
/// stays zeroed for extended ids as in every live pass. The restore also ignores mode, so if New Game
/// routes through here the previous save's extended counts return until the load edge re-seeds them
/// (the canary logs the mode).
/// Memory access goes through the injected <see cref="ICodePatcher"/>; only the original call is unwrapped.
/// </summary>
internal sealed class InventoryResetHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate nint ResetFn(int mode);

    /// <summary>First 12 bytes at the entry (see the class doc).</summary>
    public static readonly byte[] ExpectedPrologue =
        { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x56, 0x48, 0x83, 0xEC, 0x20, 0x8B, 0xD9 };

    private readonly ICodePatcher _mem;
    private readonly int _count;
    private readonly long _bagBase;
    private IHook<ResetFn>? _hook;
    private ResetFn? _keepalive;   // GC anchor. The native thunk must outlive us
    private long _runs;
    private long _restores;
    private bool _canary;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    /// <summary>How many times the game's reset ran through the detour.</summary>
    public long Runs => Interlocked.Read(ref _runs);
    /// <summary>How many of those runs had wiped the extended counts and got them put back.</summary>
    public long Restores => Interlocked.Read(ref _restores);

    /// <param name="extendedCount">N, the armed extended ids 261..261+N-1 whose bag bytes are kept.</param>
    /// <param name="bagBase">Where the bag bytes live, <see
    /// cref="ExtendedInventory.BagCountBase"/> in production (relocated page once armed). Defaults to the vanilla block.</param>
    public InventoryResetHook(ICodePatcher mem, int extendedCount, long targetAddr = 0, long bagBase = 0)
    {
        _mem = mem;
        _count = extendedCount;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;   // 0 = the vanilla block (a rebased pin cannot be a default value)
        TargetAddr = targetAddr == 0 ? Offsets.FnInventoryReset : targetAddr;
    }

    /// <summary>Pure decision. Install only when the guarded read succeeded and the entry carries
    /// <see cref="ExpectedPrologue"/> (HookLandmark.Verify's prefix compare).</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else the refusal. Idempotent.</summary>
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

    /// <summary>Detour body with the original injected (tests pass a lambda). Reads the extended bag
    /// bytes, runs the reset once, writes the bytes back if it changed them. An unreadable bag passes through.</summary>
    internal nint Process(int mode, ResetFn original)
    {
        Interlocked.Increment(ref _runs);
        long addr = _bagBase + ExtendedCatalog.FirstExtendedId;
        byte[]? before = _count > 0 && _mem.TryRead(addr, _count, out var b) ? b : null;
        nint result = original(mode);   // once, outside any try
        try { Restore(addr, before, mode); }
        catch (Exception) { /* defense in depth: the game's own state stands */ }
        return result;   // rax passes through untouched
    }

    private void Restore(long addr, byte[]? before, int mode)
    {
        if (before == null) return;
        if (!_mem.TryRead(addr, _count, out var after)) return;
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

    // Hot-path detour, must not throw. Process guards everything but the original call.
    private nint Detour(int mode) => Process(mode, _hook!.OriginalFunction);

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
