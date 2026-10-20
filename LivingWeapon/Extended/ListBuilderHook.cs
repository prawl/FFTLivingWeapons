using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Keeps the longer item lists from overrunning two small menus that build their list on the stack.
/// <see cref="Offsets.FnListBuilder"/> is the game's shared list builder, and <see cref="TemplateRelocation"/>
/// raises its cap to 255. Its eight callers hand it one of three buffers, the static 256 word
/// <see cref="Offsets.MenuListBuffer"/> or one of two 152 word stack buffers (the equip picker's weapon list and
/// the shop list), and 255 entries would run those into the stack cookie. So the hook goes by the buffer in r9
/// rather than the caller. A stack caller's list is built in a pool slot of ours and cut to
/// <see cref="StackCallerCap"/> on the way back, with the pool in .Pool.cs and the cut in .Policy.cs.
/// </summary>
internal sealed partial class ListBuilderHook
{
    // Six arguments, not four. The body reads byte ptr [rsp+0x88] (rax+0x30, the sixth argument's home slot)
    // as a per caller mode byte at four branches. Declared with four, the game read stack junk there and
    // dropped every item the unit could not equip, leaving the Items tab empty.
    [Function(CallingConventions.Microsoft)]
    public delegate int BuildFn(nint a, nint b, nint c, nint list, nint spare, nint mode);

    /// <summary>mov rax,rsp, mov [rax+0x20],r9, mov [rax+0x10],dx, mov [rax+8],cx, push rbx. Read live and the same
    /// on the 1.5.2 exe on disk, with nothing rip relative in the window.</summary>
    public static readonly byte[] ExpectedPrologue =
        { 0x48, 0x8B, 0xC4, 0x4C, 0x89, 0x48, 0x20, 0x66, 0x89, 0x50, 0x10, 0x66, 0x89, 0x48, 0x08, 0x53 };

    public const ushort Terminator = 0xFFFF;

    /// <summary>The most a 152 word stack buffer can take. 149, plus the two hand items the picker adds, plus the
    /// terminator, is (149+3)*2 = <see cref="Offsets.ListBuilderStackRoomBytes"/> (0x130, pinned by ListBuilderHookTests U1).</summary>
    public const int StackCallerCap = 149;

    /// <summary>Four slots so the builder can call back into itself, not for threads.</summary>
    public const int PoolSlots = 4;

    /// <summary>255 entries and the terminator need 0x200 bytes, and each slot gets twice that.</summary>
    public const int SlotBytes = 0x400;

    public const int PoolBytes = PoolSlots * SlotBytes;

    private readonly ICodePatcher _mem;
    private readonly INearAllocator? _alloc;
    private IHook<BuildFn>? _hook;
    private BuildFn? _keepalive;   // keeps the delegate alive as long as the native thunk points at it
    private long _poolBase;
    private readonly int[] _busy = new int[PoolSlots];
    private long _calls;
    private long _passthroughs;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    /// <summary>Calls with the big static buffer, where the game ran untouched.</summary>
    public long Passthroughs => Interlocked.Read(ref _passthroughs);

    /// <param name="allocator">Hands out the pool page at <see cref="Install"/>. Not needed when
    /// <paramref name="poolBase"/> is given.</param>
    /// <param name="poolBase">A pool page that already exists, so tests can drive <see cref="Process"/>
    /// without <see cref="Install"/>. 0 means Install asks <paramref name="allocator"/> for one.</param>
    public ListBuilderHook(ICodePatcher mem, INearAllocator? allocator = null, long targetAddr = 0, long poolBase = 0)
    {
        _mem = mem;
        _alloc = allocator;
        _poolBase = poolBase;
        TargetAddr = targetAddr == 0 ? Offsets.FnListBuilder : targetAddr;
    }

    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else why not. The widened caps are only safe with this hook in, so any
    /// refusal makes ExtendedInventory.Hooks.cs roll the whole arm back. Safe to call twice.</summary>
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

    /// <summary>The detour body, with the game's routine passed in so tests can stand in for it. The big static buffer goes
    /// straight through; any other buffer is built in a pool slot and cut to <see cref="StackCallerCap"/>.</summary>
    /// <remarks>With every slot busy the caller gets an empty list without the game running at all. Once the game has run,
    /// any failure (a bad read, a refused write, a throw) also gives back an empty list and 0, because the game's list sits
    /// in our slot and not in the caller's buffer. A throw from the game itself is not caught, it goes up to the game, and
    /// the finally still frees the slot.</remarks>
    internal int Process(nint a, nint b, nint c, nint list, nint spare, nint mode, BuildFn original)
    {
        Interlocked.Increment(ref _calls);
        if ((long)list == Offsets.MenuListBuffer)
        {
            Interlocked.Increment(ref _passthroughs);
            int passCount = original(a, b, c, list, spare, mode);   // the widened cap fits this buffer
            return passCount;
        }

        int slot = AcquireSlot();
        if (slot < 0) { PoolExhausted(list); return 0; }
        long slotAddr = SlotAddr(_poolBase, slot);
        try
        {
            int count = original(a, b, c, (nint)slotAddr, spare, mode);
            try { return CopyBack(list, slotAddr, count); }
            catch (Exception) { CopyBackRefused(list, "the copy-back threw"); return 0; }   // The caller appends at the count it gets back, so the original's count would run it past its small stack buffer
        }
        finally { ReleaseSlot(slot); }   // freed right away so a nested call can reuse it
    }

    // Runs inside the game, so nothing past the game's own call may throw.
    private int Detour(nint a, nint b, nint c, nint list, nint spare, nint mode) => Process(a, b, c, list, spare, mode, _hook!.OriginalFunction);

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
