using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// LW-372 (plan v1.2): detour on the game's shared item-list builder (<see cref="Offsets.FnListBuilder"/>),
/// the routine whose own entry-cap byte <see cref="TemplateRelocation"/> widens to 255 (D4). Plain
/// language: raising that cap alone is not safe for every caller. Eight call sites hand the
/// builder one of exactly three buffers (LW-371 P16): the game's own static 256-word notepad
/// (<see cref="Offsets.MenuListBuffer"/>, which the widened cap fits), or one of two small
/// 152-word STACK notepads (the equip picker's weapons-only list, a shop's list) that a 255-entry
/// write would blow straight through the stack security cookie -- an uncatchable process kill the
/// moment a player owns 150+ weapon-class kinds. This hook lets the two small-notepad callers
/// keep running against a buffer the widened builder cannot overrun (a mod-owned pool slot),
/// truncates the result back down to the LW-371-proven-safe <see cref="StackCallerCap"/> (149)
/// entries before it ever reaches their real stack buffer, and leaves every static-buffer caller
/// completely alone.
///
/// D2's rule is keyed on the BUFFER the caller hands in as r9, not on which of the eight call
/// sites is running (Q3/Q10: caller identity is left open on purpose; buffer identity is the only
/// thing correctness depends on).
///
/// This file is the D2 half (buffer-routing decision, lifecycle, detour plumbing); the D3 pool
/// slot lifecycle (acquire/release/exhaustion, the copy-back read, F1's refusal posture) lives in
/// ListBuilderHook.Pool.cs, and the pure truncation math (byte encoding, slot-address arithmetic)
/// in ListBuilderHook.Policy.cs -- the same three-way split OrderRebuildHook uses for its own
/// distinct D2/seat/policy concerns. Every memory access inside the detour goes through the
/// injected <see cref="ICodePatcher"/> (kernel-guarded, never faults).
///
/// D7's pool-path posture, named: the call to the original is the only statement never wrapped in
/// a try/catch, and it sits inside a try/FINALLY WITH NO CATCH OF ITS OWN, so a game exception
/// still propagates (nothing here swallows it) while the busy pool slot can never leak -- the
/// InventoryResetHook idiom (guard everything, call the original unwrapped) cannot release a slot
/// on a throw the way a bare try/catch would swallow one, and a leaked slot would eventually brick
/// every stack-buffer menu (four slots, never refilled). The copy-back's own failures (F1, v1.2)
/// are handled one level in, by ListBuilderHook.Pool.cs's CopyBack/CopyBackRefused.
/// </summary>
internal sealed partial class ListBuilderHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate int BuildFn(nint a, nint b, nint c, nint list);

    /// <summary>First 16 bytes at the entry (Q2, live-read 2026-08-31, confirmed byte-identical on
    /// the 1.5.2 exe on disk): mov rax,rsp; mov [rax+0x20],r9; two word-arg spills (mov
    /// [rax+0x10],dx; mov [rax+8],cx -- the a/b/c parameters the function only ever reads the low
    /// bits of); push rbx. No rip-relative field inside this window.</summary>
    public static readonly byte[] ExpectedPrologue =
        { 0x48, 0x8B, 0xC4, 0x4C, 0x89, 0x48, 0x20, 0x66, 0x89, 0x50, 0x10, 0x66, 0x89, 0x48, 0x08, 0x53 };

    public const ushort Terminator = 0xFFFF;

    /// <summary>D2: the LW-371-proven-safe cap for a 152-word stack buffer -- cap + the picker's
    /// two hand-item appends + terminator = (149+3)*2 = <see cref="Offsets.ListBuilderStackRoomBytes"/>
    /// exactly (0x130, pinned by ListBuilderHookTests U1). The SAME constant the truncation copy-back
    /// uses, never a copied literal.</summary>
    public const int StackCallerCap = 149;

    /// <summary>D3: four try-acquire slots on one page, for REENTRANCY, not concurrency.</summary>
    public const int PoolSlots = 4;

    /// <summary>D3: bytes per slot. A 255-entry list + terminator needs 256 words (0x200 bytes);
    /// each slot is 2x that -- headroom, not an exact fit.</summary>
    public const int SlotBytes = 0x400;

    /// <summary>The whole pool page requested from <see cref="INearAllocator"/> at Install time.</summary>
    public const int PoolBytes = PoolSlots * SlotBytes;

    private readonly ICodePatcher _mem;
    private readonly INearAllocator? _alloc;
    private IHook<BuildFn>? _hook;
    private BuildFn? _keepalive;   // GC anchor: the native thunk must outlive us
    private long _poolBase;
    private readonly int[] _busy = new int[PoolSlots];   // D3 pool-lifecycle state; see ListBuilderHook.Pool.cs
    private long _calls;
    private long _passthroughs;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    /// <summary>Calls the widened builder cap already covers on its own: the caller handed in
    /// <see cref="Offsets.MenuListBuffer"/> itself, so the hook ran the original unchanged.</summary>
    public long Passthroughs => Interlocked.Read(ref _passthroughs);

    /// <param name="allocator">Grants the stack-caller pool page at <see cref="Install"/> time
    /// (production). Leave null when <paramref name="poolBase"/> is already given -- the test
    /// seam: ListBuilderHookTests drives <see cref="Process"/> directly, without ever calling
    /// <see cref="Install"/> or touching a real <see cref="IReloadedHooks"/>.</param>
    /// <param name="poolBase">A pre-allocated pool page address (test seam). 0 (production)
    /// means <see cref="Install"/> must allocate one through <paramref name="allocator"/> before
    /// the hook can arm.</param>
    public ListBuilderHook(ICodePatcher mem, INearAllocator? allocator = null, long targetAddr = 0, long poolBase = 0)
    {
        _mem = mem;
        _alloc = allocator;
        _poolBase = poolBase;
        TargetAddr = targetAddr == 0 ? Offsets.FnListBuilder : targetAddr;
    }

    /// <summary>Pure decision: install only when the guarded read succeeded and the entry carries
    /// <see cref="ExpectedPrologue"/> (HookLandmark.Verify's prefix compare).</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else the refusal. D8: the cap-byte patches and this hook are one
    /// transaction -- a refusal here (bad prologue, or no pool page) must roll the whole extended-
    /// inventory arm back, which ExtendedInventory.Hooks.cs's caller already does on any non-null
    /// return. Idempotent.</summary>
    public string? Install(IReloadedHooks hooks)
    {
        if (_hook != null) { if (!_hook.IsHookEnabled) _hook.Enable(); return null; }
        bool ok = _mem.TryRead(TargetAddr, ExpectedPrologue.Length, out var entry);
        if (!ShouldArm(ok, entry))
            return $"list-builder: 0x{TargetAddr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        if (_poolBase == 0)
        {
            if (_alloc == null)
                return "list-builder: no allocator was given for the stack-caller pool";
            long page = _alloc.Alloc(PoolBytes, Offsets.ModuleBase);
            if (page == 0)
                return "list-builder: no page within reach for the stack-caller pool";
            _poolBase = page;
        }
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<BuildFn>(_keepalive, TargetAddr).Activate();
            return null;
        }
        catch (Exception ex) { return $"list-builder: hook install failed ({ex.Message})"; }
    }

    public void Release()
    {
        try { if (_hook?.IsHookEnabled == true) _hook.Disable(); }
        catch (Exception ex) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, "list-builder release swallowed: " + ex.Message)); }
    }

    /// <summary>The whole detour body with the original injected (tests pass a lambda). D2: a
    /// static-buffer call is a pure passthrough. Any other buffer is routed through a pool slot
    /// (ListBuilderHook.Pool.cs) and the result truncated to <see cref="StackCallerCap"/> on the
    /// way back to the caller's real buffer. D3/Q11: pool exhaustion never calls the original at
    /// all -- there is no buffer left it would be safe to hand it, so the only safe answer is an
    /// empty list. F1 (v1.2): every failure past that point -- CopyBack's own failed read/refused
    /// write, or an exception this catch alone reaches (e.g. a throw on the read itself, before
    /// CopyBack's own best-effort write ever runs) -- also answers with an empty list and 0, NEVER
    /// the original's own count (its world lives in the pool slot, not the caller's buffer).</summary>
    internal int Process(nint a, nint b, nint c, nint list, BuildFn original)
    {
        Interlocked.Increment(ref _calls);
        if ((long)list == Offsets.MenuListBuffer)
        {
            Interlocked.Increment(ref _passthroughs);
            return original(a, b, c, list);   // exactly once, never inside a try: the widened cap already applies here
        }

        int slot = AcquireSlot();
        if (slot < 0) { PoolExhausted(list); return 0; }
        long slotAddr = SlotAddr(_poolBase, slot);
        try
        {
            int count = original(a, b, c, (nint)slotAddr);   // exactly once, never inside a try/catch (the finally is deliberate: D7's pool posture, see the class doc)
            try { return CopyBack(list, slotAddr, count); }
            catch (Exception) { CopyBackRefused(list, "the copy-back threw"); return 0; }   // F1: never the original's own count
        }
        finally { ReleaseSlot(slot); }   // D3: released the instant the copy-back is done, reentrant-safe
    }

    // Hot-path detour: MUST NOT throw. Process guards everything but the original call itself.
    private int Detour(nint a, nint b, nint c, nint list) => Process(a, b, c, list, _hook!.OriginalFunction);

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
