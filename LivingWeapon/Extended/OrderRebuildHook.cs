using System;
using System.Threading;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace LivingWeapon;

/// <summary>
/// Puts the new items back into menu lists after the game rebuilds them without them.
/// The Inventory tabs, the equip picker and the Acquired sort are rebuilt (<see cref="Offsets.FnOrderRebuild"/>)
/// from fixed order charts that hold no new item id, so the game drops ours. The hook lets the rebuild run,
/// adds the dropped words back before the terminator and fixes the count. Ported from the FFTHandsFree
/// rig's OrderRebuildHook and watched on all three list paths [journal 2026-08-26].
///
/// The target is <c>int Rebuild(nint table /*rcx*/, nint list /*rdx*/)</c> over u16 words
/// (<c>id | flags</c>, bit 14 the E badge) ending in 0xFFFF. It keeps part of the input in chart order and
/// returns how many it kept. Entry bytes read live and confirmed on the 1.5.2 exe on disk [journal 2026-08-27]
/// (<see cref="ExpectedPrologue"/>, with ret and CC padding just before the entry). The list parsing lives
/// in OrderRebuildHook.Policy.cs.
/// </summary>
internal sealed partial class OrderRebuildHook
{
    [Function(CallingConventions.Microsoft)]
    public delegate int RebuildFn(nint table, nint list);

    /// <summary>mov [rsp+8],rbx then mov [rsp+18h],rbp.</summary>
    public static readonly byte[] ExpectedPrologue = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x6C, 0x24, 0x18 };

    /// <summary>The list builder now goes up to 255 entries, so 260 words leaves room to spare.</summary>
    /// <remarks>On the 512 byte static buffer (<see cref="Offsets.MenuListBuffer"/>) that reads 8 bytes past its end
    /// at +0x200, which is mapped and harmless, since the read is guarded and <see cref="ParseList"/> stops
    /// at the terminator (null if there is none).</remarks>
    public const int MaxWords = 260;
    public const ushort Terminator = 0xFFFF;
    // The low ten bits of a word are the item id and the bits above are badges.
    public const int IdMask = 0x3FF;
    /// <summary>The E badge, set while every copy is worn. Such an item shows 0 in the bag and is
    /// still owned, so it has to come back too.</summary>
    public const int EquippedBadge = 0x4000;

    private readonly ICodePatcher _mem;
    private IHook<RebuildFn>? _hook;
    private RebuildFn? _keepalive;   // keeps the delegate alive as long as the native thunk points at it
    private long _calls;
    private long _reappended;

    public long TargetAddr { get; }
    public bool IsActive => _hook?.IsHookEnabled == true;
    public long Calls => Interlocked.Read(ref _calls);
    public long Reappended => Interlocked.Read(ref _reappended);

    /// <param name="extendedCount">How many new items exist, ids 261 up to 261+N-1, for the seat step in
    /// OrderRebuildHook.Seat.cs. 0 seats nothing new, though a chart still gets repaired, and the add back stays as the fallback.</param>
    /// <param name="bagBase">Where the bag counts live, <see
    /// cref="ExtendedInventory.BagCountBase"/> in production and the game's own block when left at 0.</param>
    /// <param name="regions">Where the two weapon order charts live, <see
    /// cref="ExtendedInventory.TemplateRegions"/> in production. Null uses
    /// <see cref="TemplateSeat.WeaponRegions"/> when called.</param>
    public OrderRebuildHook(ICodePatcher mem, long targetAddr = 0, int extendedCount = 0,
        long bagBase = 0, Func<TemplateSeat.Region[]>? regions = null)
    {
        _mem = mem;
        _extendedCount = extendedCount;
        _bagBase = bagBase == 0 ? Offsets.BagCountArray : bagBase;   // a rebased address cannot be a default parameter
        _regions = regions;
        TargetAddr = targetAddr == 0 ? Offsets.FnOrderRebuild : targetAddr;
    }

    internal static bool ShouldArm(bool readOk, byte[]? prologue) => readOk && HookLandmark.Verify(prologue, ExpectedPrologue);

    /// <summary>Null on success, else why not. Safe to call twice.</summary>
    public string? Install(IReloadedHooks hooks)
    {
        if (_hook != null) { if (!_hook.IsHookEnabled) _hook.Enable(); return null; }
        bool ok = _mem.TryRead(TargetAddr, ExpectedPrologue.Length, out var entry);
        if (!ShouldArm(ok, entry))
            return $"order-rebuild: 0x{TargetAddr:X} does not carry the expected prologue (reads {(ok ? Convert.ToHexString(entry) : "unreadable")})";
        try
        {
            _keepalive = Detour;
            _hook = hooks.CreateHook<RebuildFn>(_keepalive, TargetAddr);
            _hook.Activate();
            return null;
        }
        catch (Exception ex) { Release(); _hook = null; return $"order-rebuild: hook install failed ({ex.Message})"; }
    }

    public void Release()
    {
        try { if (_hook?.IsHookEnabled == true) _hook.Disable(); }
        catch (Exception ex) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, "order-rebuild release swallowed: " + ex.Message)); }
    }

    /// <summary>The detour body, with the game's routine passed in so tests can stand in for it.
    /// If either read of the list fails, the game's own count stands.</summary>
    internal int Process(nint table, nint list, RebuildFn original)
    {
        // Two tries at one job. Slip the new items into the game's order chart so it keeps them itself, then add back anything it still dropped.
        try { SeatOwnedInto(table); }
        catch (Exception) { /* the game's own rebuild still runs on whatever the chart holds */ }
        var before = ReadListOrNull(list);
        int count = original(table, list);   // once, and outside any try so a game exception reaches the game
        try { return Reappend(before, table, list, count); }
        catch (Exception) { return count; }   // a throw here leaves the game's own answer
    }

    private int Reappend(ushort[]? before, nint table, nint list, int count)
    {
        if (before == null) return count;
        var after = ReadListOrNull(list);
        if (after == null) return count;

        // Only an id the bag still holds comes back. The old list can carry an id since sold or
        // equipped away, and adding that back once showed an empty row.
        var dropped = Array.FindAll(DroppedWords(before, after), w => Owned(w & IdMask) || (w & EquippedBadge) != 0);
        if (dropped.Length == 0) return count;
        // A chart that lists an id twice could push the list past the space the caller gave it.
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

    // Runs inside the game, so nothing past the game's own call may throw.
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
