using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Detour on the game's item-list rebuild routine (<see cref="Offsets.FnOrderRebuild"/>).
/// Menu lists (Inventory tabs, equip picker, "Acquired" sort) are rebuilt from fixed order tables
/// that contain no new item id, so the routine drops it. The hook lets the rebuild run, re-appends
/// the dropped words before the terminator and corrects the count. Ported from the FFTHandsFree
/// rig's OrderRebuildHook (b1abd77), watched on all three list paths 2026-08-26 23:05.
///
/// Target <c>int Rebuild(nint table /*rcx*/, nint list /*rdx*/)</c> over u16 words
/// (<c>id | flags</c>, bit 14 = E-badge) ending in 0xFFFF. It keeps a subset of the input in table
/// order and returns the count kept. Entry bytes read 2026-08-26, confirmed on the 1.5.2 exe on
/// disk 2026-08-27 (<see cref="ExpectedPrologue"/>, ret+CC padding before the entry). Parsing lives
/// in OrderRebuildHook.Policy.cs; memory access goes through <see cref="ICodePatcher"/>.
/// </summary>
internal sealed partial class OrderRebuildHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate int RebuildFn(nint table, nint list);

    /// <summary>First 10 bytes at the entry. Mov [rsp+8],rbx; mov [rsp+18h],rbp.</summary>
    public static readonly byte[] ExpectedPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x18 };

    /// <summary>Upper bound on words read per call. The list builder's cap is widened to 255
    /// entries, so 260 words leaves 4 of headroom. Reading 520 bytes off the 512-byte static buffer
    /// (<see cref="Offsets.MenuListBuffer"/>) overshoots by 8 mapped bytes at +0x200; benign, since the
    /// read is guarded and <see cref="ParseList"/> stops at the terminator (null if none).</summary>
    public const int MaxWords = 260;
    public const ushort Terminator = 0xFFFF;
    public const int IdMask = 0x3FF;
    /// <summary>Bit 14 of a list word. The E badge, set while every copy is worn. Found
    /// 2026-08-30, all-equipped designs read bag 0 but are owned, so re-append honors the badge.</summary>
    public const int EquippedBadge = 0x4000;

    private readonly ICodePatcher _mem;
    private IHook<RebuildFn>? _hook;
    private RebuildFn? _keepalive;   // GC anchor. The native thunk must outlive us
    private long _calls;
    private long _reappended;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    public long Reappended => Interlocked.Read(ref _reappended);

    /// <param name="extendedCount">N, the armed extended ids 261..261+N-1 the seat-before-rebuild
    /// step (OrderRebuildHook.Seat.cs) may seat; 0 = no seating, re-append fallback only.</param>
    /// <param name="bagBase">Where the bag bytes live, <see
    /// cref="ExtendedInventory.BagCountBase"/> in production. Defaults to the vanilla block.</param>
    /// <param name="regions">where the two weapon order templates live, <see
    /// cref="ExtendedInventory.TemplateRegions"/> in production. Null falls back to
    /// <see cref="TemplateSeat.WeaponRegions"/> at call time.</param>
    public OrderRebuildHook(ICodePatcher mem, long targetAddr = 0, int extendedCount = 0,
        long bagBase = 0, Func<TemplateSeat.Region[]>? regions = null)
    {
        _mem = mem;
        _extendedCount = extendedCount;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;   // 0 = the vanilla block (a rebased pin cannot be a default value)
        _regions = regions;
        TargetAddr = targetAddr == 0 ? Offsets.FnOrderRebuild : targetAddr;
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
            return $"order-rebuild: 0x{TargetAddr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<RebuildFn>(_keepalive, TargetAddr).Activate();
            return null;
        }
        catch (Exception ex) { return $"order-rebuild: hook install failed ({ex.Message})"; }
    }

    public void Release()
    {
        try { if (_hook?.IsHookEnabled == true) _hook.Disable(); }
        catch (Exception ex) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, "order-rebuild release swallowed: " + ex.Message)); }
    }

    /// <summary>The detour body with the original injected (tests pass a lambda): read the list
    /// before and after the game's rebuild, re-append what it dropped, return the corrected count.
    /// A failed read on either side passes through.</summary>
    internal int Process(nint table, nint list, RebuildFn original)
    {
        try { SeatOwnedInto(table); }   // fix round 7. Owned ids go into the template FIRST
        catch (Exception) { /* the game's own rebuild still runs on whatever the template holds */ }
        var before = ReadListOrNull(list);
        int count = original(table, list);   // exactly once, never inside a try
        try { return Reappend(before, table, list, count); }
        catch (Exception) { return count; }   // defense in depth. The game's own answer stands
    }

    private int Reappend(ushort[]? before, nint table, nint list, int count)
    {
        if (before == null) return count;
        var after = ReadListOrNull(list);
        if (after == null) return count;

        // Fix round 7. Only an id the bag still holds comes back. The previous list can carry an id
        // since sold or equipped away, and re-appending it seated an empty row (2026-08-30 23:34).
        var dropped = Array.FindAll(DroppedWords(before, after), w => Owned(w & IdMask) || (w & EquippedBadge) != 0);
        if (dropped.Length == 0) return count;
        // Never write past the input's own footprint, even if a table lists an id twice.
        if (after.Length + dropped.Length > before.Length)
        {
            SafeLog(() => ModLogger.Warn(LogVerb.Engine,
                $"The item list at 0x{(long)list:X} could not take back {dropped.Length} dropped id(s): {after.Length}+{dropped.Length} would exceed its own {before.Length}-word footprint."));
            return count;
        }
        if (!_mem.TryWrite((long)list + after.Length * 2, TailBytes(dropped)))
        {
            SafeLog(() => ModLogger.Warn(LogVerb.Engine, $"The item list at 0x{(long)list:X} refused the re-append write."));
            return count;
        }
        Interlocked.Add(ref _reappended, dropped.Length);
        var ids = string.Join(",", Array.ConvertAll(dropped, w => (w & IdMask).ToString()));
        SafeLog(() => ModLogger.Debug(LogVerb.Engine,
            $"Re-appended {dropped.Length} extended item id(s) [{ids}] the menu rebuild dropped (list 0x{(long)list:X}, table 0x{(long)table:X}, count {count} -> {after.Length + dropped.Length})."));
        return after.Length + dropped.Length;
    }

    private ushort[]? ReadListOrNull(nint list)
        => _mem.TryRead((long)list, MaxWords * 2, out var bytes) ? ParseList(bytes) : null;

    // Hot-path detour. Must not throw. Process guards everything but the original call.
    private int Detour(nint table, nint list)
    {
        Interlocked.Increment(ref _calls);
        return Process(table, list, _hook!.OriginalFunction);
    }

    private static void SafeLog(Action log)
    {
        try { log(); } catch (Exception) { /* a logger failure must never escape the detour */ }
    }
}
