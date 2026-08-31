using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// LW-346: detour on the game's item-list rebuild routine (<see cref="Offsets.FnOrderRebuild"/>).
/// Several menu lists (Inventory tabs, the equip picker, the "Acquired" sort) are rebuilt from
/// fixed display-order tables that no new item id is in, so the routine silently drops it. This
/// hook lets the game's own rebuild run first, then re-appends whatever word(s) it dropped onto
/// the end of the list, in their original order, ahead of the terminator, and corrects the
/// returned count. Ported from the FFTHandsFree rig's OrderRebuildHook (b1abd77), whose live
/// pass the owner watched on all three list paths 2026-08-26 23:05.
///
/// Contract of the target: <c>int Rebuild(nint table /*rcx*/, nint list /*rdx*/)</c>; the list
/// is u16 words (<c>id | flags</c>, bit 14 = E-badge) terminated by 0xFFFF; the routine copies
/// list words into a stack temp in table order, memcpys the temp back over the list (the output
/// is a SUBSET of the input, never longer), writes 0xFFFF after it and returns the words kept.
/// Entry bytes read live 2026-08-26 and confirmed on the 1.5.2 exe on disk 2026-08-27
/// (<see cref="ExpectedPrologue"/>, ret+CC padding before the entry).
///
/// Pure list parsing/diffing lives in OrderRebuildHook.Policy.cs. Every memory access inside the
/// detour goes through the injected <see cref="ICodePatcher"/> (kernel-guarded, never faults);
/// the call to the original is the only thing never wrapped.
/// </summary>
internal sealed partial class OrderRebuildHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate int RebuildFn(nint table, nint list);

    /// <summary>First 10 bytes at the entry: mov [rsp+8],rbx; mov [rsp+18h],rbp.</summary>
    public static readonly byte[] ExpectedPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x18 };

    /// <summary>Upper bound on words read per call: builder A caps lists at 145 entries, the
    /// Acquired scan at 146 (journal, "List-length bounds"); 160 bounds every speculative read.</summary>
    public const int MaxWords = 160;
    public const ushort Terminator = 0xFFFF;
    public const int IdMask = 0x3FF;
    /// <summary>Bit 14 of a list word: the game's E badge, set while every copy of the item is
    /// worn. LW-351 round 7 verify (2026-08-30): a design whose only copies are equipped reads
    /// bag 0 yet is still owned, so the owned-only re-append must honor the badge too or the
    /// rebuild orphans it until a copy returns to the bag.</summary>
    public const int EquippedBadge = 0x4000;

    private readonly ICodePatcher _mem;
    private IHook<RebuildFn>? _hook;
    private RebuildFn? _keepalive;   // GC anchor: the native thunk must outlive us
    private long _calls;
    private long _reappended;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    public long Reappended => Interlocked.Read(ref _reappended);

    /// <param name="extendedCount">N, the armed extended ids 261..261+N-1 the seat-before-rebuild
    /// step (OrderRebuildHook.Seat.cs) may seat; 0 = no seating, the re-append fallback only.</param>
    /// <param name="bagBase">LW-368 round 2: where the bag bytes live -- <see
    /// cref="ExtendedInventory.BagCountBase"/> in production (the relocated page once armed,
    /// else the vanilla block); defaults to the vanilla block so every pre-existing caller keeps
    /// compiling unchanged.</param>
    public OrderRebuildHook(ICodePatcher mem, long targetAddr = 0, int extendedCount = 0, long bagBase = Offsets.BagCountArray)
    {
        _mem = mem;
        _extendedCount = extendedCount;
        _bagBase = bagBase;
        TargetAddr = targetAddr == 0 ? Offsets.FnOrderRebuild : targetAddr;
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

    /// <summary>The whole detour body with the original injected (tests pass a lambda): read the
    /// list before and after the game's own rebuild, re-append what it dropped, return the
    /// corrected count. A failed read on either side is a silent passthrough.</summary>
    internal int Process(nint table, nint list, RebuildFn original)
    {
        try { SeatOwnedInto(table); }   // fix round 7: owned ids go into the template FIRST
        catch (Exception) { /* the game's own rebuild still runs on whatever the template holds */ }
        var before = ReadListOrNull(list);
        int count = original(table, list);   // exactly once, never inside a try
        try { return Reappend(before, table, list, count); }
        catch (Exception) { return count; }   // defense in depth: the game's own answer stands
    }

    private int Reappend(ushort[]? before, nint table, nint list, int count)
    {
        if (before == null) return count;
        var after = ReadListOrNull(list);
        if (after == null) return count;

        // Fix round 7: only an id the bag still holds comes back. The previous list can carry an
        // id the player has since sold or equipped away; re-appending it seated a row the game
        // had no item for (the owner's "empty item in my inventory list", 2026-08-30 23:34).
        var dropped = Array.FindAll(DroppedWords(before, after), w => Owned(w & IdMask) || (w & EquippedBadge) != 0);
        if (dropped.Length == 0) return count;
        // The write must never exceed the input's own footprint, even if a table lists an id twice.
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

    // Hot-path detour: MUST NOT throw. Process guards everything but the original call itself.
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
