using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// LW-351 fix round 7 (2026-08-30): detour on the game's per-item inventory reset
/// (<see cref="Offsets.FnInventoryReset"/>, 0x140284500). Plainly: the game has a routine that
/// wipes its "how many of each item do you own" table and rebuilds it from the save; it only
/// rebuilds the base game's 261 items, so the new items' counts came back as zero after every
/// real battle (owner-observed 2026-08-30 23:29: Terrastaff x2, Ravager x3, Sunderer x3 ... all
/// x0 after one fight; no load edge fired in between). This hook remembers the new items' counts
/// before the routine runs and puts them back afterwards.
///
/// WHY THE WIPE EXISTS AT ALL: fix round 2 widened this routine's loop bound (0x140284554,
/// 0x105 -&gt; 0x105 + N) so the per-item state arrays it seeds cover the new ids too (that cured
/// the stale-word holes in the menu order templates). The same loop's first pass is
/// <c>for id in 0..bound: byte [0x1411A7C00 + id] = 0</c> (0x140284561, the bag array), so the
/// widening made it zero the extended counts as well, and nothing downstream refills 261+.
///
/// Read from the 1.5.2 exe on disk 2026-08-30 (capstone):
///   140284500  48 89 5c 24 08   mov [rsp+8],rbx      \
///   140284505  56               push rsi              | the 12-byte prologue this hook verifies
///   140284506  48 83 ec 20      sub rsp,0x20          |
///   14028450A  8b d9            mov ebx,ecx           / ecx = mode (0, 2, or other)
///   ...        0x36-entry table at stride 0x258 filled with 0xFF; mode 0 calls 0x14027528C(2),
///              mode 2 calls 0x1402841D0(0xFE); then the zero loop over the bag array, a second
///              byte array (0x1411A7700), a 0x80-byte array, the u16 array seeded with 0x0101,
///              three memsets, a 0x61-entry u16 seed, three helpers (0x1402842CC, 0x1402E9918,
///              0x14028433C) and, for mode 0, 0x1402F0B68; ends `ret` at 0x14028467C.
///   14028467D  call 0x1404200F8; int3   = the range-check abort the loops' `jae` land on.
/// No direct caller exists in plain code (indirect dispatch), so the canary log line is the
/// proof of which game moment runs it. No caller could be found to prove eax unread, so the
/// delegate is <c>nint Reset(int mode)</c> and the detour hands rax back untouched.
/// Two deliberate non-restores, named so nobody reads them as oversights: the second byte
/// array 0x1411A7700 (261 bytes of per-item save state at struct +0x84AD) is zeroed for the
/// extended ids and left that way, which is exactly the post-load state every live pass
/// listed, equipped and fought in; and the restore is mode-blind, so if New Game routes
/// through this routine the previous save's extended counts come back until the load edge
/// re-seeds them (the canary logs the mode; the owner's live check covers New Game).
///
/// Every memory access goes through the injected <see cref="ICodePatcher"/> (kernel-guarded);
/// the call to the original is the only thing never wrapped.
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
    private ResetFn? _keepalive;   // GC anchor: the native thunk must outlive us
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
    /// <param name="bagBase">LW-368 round 2: where the bag bytes live -- <see
    /// cref="ExtendedInventory.BagCountBase"/> in production (the relocated page once armed,
    /// else the vanilla block); defaults to the vanilla block so every pre-existing caller keeps
    /// compiling unchanged.</param>
    public InventoryResetHook(ICodePatcher mem, int extendedCount, long targetAddr = 0, long bagBase = Offsets.BagCountArray)
    {
        _mem = mem;
        _count = extendedCount;
        _bagBase = bagBase;
        TargetAddr = targetAddr == 0 ? Offsets.FnInventoryReset : targetAddr;
    }

    /// <summary>Pure decision: install only when the guarded read succeeded and the entry carries
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

    /// <summary>The whole detour body with the original injected (tests pass a lambda): read the
    /// extended bag bytes, run the game's reset exactly once, write the bytes back if the reset
    /// changed them. An unreadable bag is a silent passthrough (the game's answer stands).</summary>
    internal nint Process(int mode, ResetFn original)
    {
        Interlocked.Increment(ref _runs);
        long addr = _bagBase + ExtendedCatalog.FirstExtendedId;
        byte[]? before = _count > 0 && _mem.TryRead(addr, _count, out var b) ? b : null;
        nint result = original(mode);   // exactly once, never inside a try
        try { Restore(addr, before, mode); }
        catch (Exception) { /* defense in depth: the game's own state stands */ }
        return result;   // rax passes through untouched: no caller was found to prove eax unread
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

    // Hot-path detour: MUST NOT throw. Process guards everything but the original call itself.
    private nint Detour(int mode) => Process(mode, _hook!.OriginalFunction);

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
