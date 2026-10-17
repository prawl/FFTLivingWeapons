using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Detour on the game's shared item-list builder (<see cref="Offsets.FnListBuilder"/>), whose
/// cap byte <see cref="TemplateRelocation"/> widens to 255 (D4). Eight call sites pass one of three
/// buffers. The static 256-word <see cref="Offsets.MenuListBuffer"/>, or one of two 152-word
/// stack buffers (equip picker weapons list, shop list) that 255 entries would overrun into the stack
/// cookie. D2 keys on the buffer in r9, not the call site. Stack callers get a mod-owned pool slot and
/// the result is cut to <see cref="StackCallerCap"/>. Pool lifecycle is in ListBuilderHook.Pool.cs and
/// truncation math in ListBuilderHook.Policy.cs. Memory access goes through <see cref="ICodePatcher"/>.
/// </summary>
internal sealed partial class ListBuilderHook
{
    // The target takes six arguments. F1 disasm. The body reads byte ptr [rsp+0x88] (rax+0x30,
    // home slot of argument 6) as a per-caller mode byte at four branches. Declaring only four let the
    // callee read stack junk and drop every unequippable id (empty Items tab). spare and mode pass through untouched.
    [Function(CallingConventions.Microsoft)]
    public delegate int BuildFn(nint a, nint b, nint c, nint list, nint spare, nint mode);

    /// <summary>First 16 bytes at the entry (Q2, live-read 2026-08-31, byte-identical on the 1.5.2 exe
    /// on disk): mov rax,rsp; mov [rax+0x20],r9; mov [rax+0x10],dx; mov [rax+8],cx; push rbx.
    /// No rip-relative field in the window.</summary>
    public static readonly byte[] ExpectedPrologue =
        { 0x48, 0x8B, 0xC4, 0x4C, 0x89, 0x48, 0x20, 0x66, 0x89, 0x50, 0x10, 0x66, 0x89, 0x48, 0x08, 0x53 };

    public const ushort Terminator = 0xFFFF;

    /// <summary>Cap for a 152-word stack buffer. 149 + the picker's two hand-item appends +
    /// terminator = (149+3)*2 = <see cref="Offsets.ListBuilderStackRoomBytes"/> (0x130, pinned by
    /// ListBuilderHookTests U1). The copy-back reuses this constant.</summary>
    public const int StackCallerCap = 149;

    /// <summary>D3. Four slots on one page, for reentrancy rather than concurrency.</summary>
    public const int PoolSlots = 4;

    /// <summary>D3. Bytes per slot. 255 entries + terminator need 0x200 bytes; each slot is twice that.</summary>
    public const int SlotBytes = 0x400;

    /// <summary>Pool page requested from <see cref="INearAllocator"/> at Install.</summary>
    public const int PoolBytes = PoolSlots * SlotBytes;

    private readonly ICodePatcher _mem;
    private readonly INearAllocator? _alloc;
    private IHook<BuildFn>? _hook;
    private BuildFn? _keepalive;   // GC anchor. The native thunk must outlive us
    private long _poolBase;
    private readonly int[] _busy = new int[PoolSlots];   // D3 pool-lifecycle state; see ListBuilderHook.Pool.cs
    private long _calls;
    private long _passthroughs;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    /// <summary>Calls where the caller passed <see cref="Offsets.MenuListBuffer"/>, so the original ran unchanged.</summary>
    public long Passthroughs => Interlocked.Read(ref _passthroughs);

    /// <param name="allocator">Grants the pool page at <see cref="Install"/> (production). Null when
    /// <paramref name="poolBase"/> is given.</param>
    /// <param name="poolBase">Pre-allocated pool page (test seam. ListBuilderHookTests drives
    /// <see cref="Process"/> without <see cref="Install"/>). 0 means Install allocates one through
    /// <paramref name="allocator"/>.</param>
    public ListBuilderHook(ICodePatcher mem, INearAllocator? allocator = null, long targetAddr = 0, long poolBase = 0)
    {
        _mem = mem;
        _alloc = allocator;
        _poolBase = poolBase;
        TargetAddr = targetAddr == 0 ? Offsets.FnListBuilder : targetAddr;
    }

    /// <summary>Install only when the guarded read succeeded and the entry matches
    /// <see cref="ExpectedPrologue"/> (HookLandmark.Verify prefix compare).</summary>
    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else the refusal. D8. The cap-byte patches and this hook are one
    /// transaction, so a refusal rolls back the extended-inventory arm (ExtendedInventory.Hooks.cs does
    /// this on any non-null return). Idempotent.</summary>
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
            long page = _alloc.Alloc(PoolBytes, ImageBase.Base);
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

    /// <summary>Detour body with the original injected for tests. D2. The static buffer passes through;
    /// any other buffer goes through a pool slot (ListBuilderHook.Pool.cs) and the result is truncated to
    /// <see cref="StackCallerCap"/>. D3/Q11. Pool exhaustion returns an empty list without calling the
    /// original. F1 (v1.2): any failure after the original ran (failed read, refused write, a throw) also
    /// returns an empty list and 0, never the original's count, since its list sits in the pool slot.
    /// D7. The original call has no catch but sits in try/finally, so a game exception propagates and the
    /// slot is still released. CopyBack's own failures are handled in Pool.cs.</summary>
    internal int Process(nint a, nint b, nint c, nint list, nint spare, nint mode, BuildFn original)
    {
        Interlocked.Increment(ref _calls);
        if ((long)list == Offsets.MenuListBuffer)
        {
            Interlocked.Increment(ref _passthroughs);
            int passCount = original(a, b, c, list, spare, mode);   // called once, outside any try; the widened cap applies here
            return passCount;
        }

        int slot = AcquireSlot();
        if (slot < 0) { PoolExhausted(list); return 0; }
        long slotAddr = SlotAddr(_poolBase, slot);
        try
        {
            int count = original(a, b, c, (nint)slotAddr, spare, mode);   // called once, no catch (D7, see Process)
            try { return CopyBack(list, slotAddr, count); }
            catch (Exception) { CopyBackRefused(list, "the copy-back threw"); return 0; }   // F1. Never the original's own count
        }
        finally { ReleaseSlot(slot); }   // D3. Released the instant the copy-back is done, reentrant-safe
    }

    // Hot path, must not throw. Process guards all but the original call.
    private int Detour(nint a, nint b, nint c, nint list, nint spare, nint mode) => Process(a, b, c, list, spare, mode, _hook!.OriginalFunction);

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
