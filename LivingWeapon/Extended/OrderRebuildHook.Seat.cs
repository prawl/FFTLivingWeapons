using System;
using System.Collections.Generic;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// LW-351 fix round 7 (2026-08-30): the seat-before-rebuild step and the ownership filter.
/// Plainly: the game rebuilds its item menus from a saved order list, and a new item bought this
/// session is not on that list until the next save is loaded, so the rebuild kept dropping it;
/// the old answer (re-append whatever was dropped, afterwards) failed whenever the rebuilt list
/// was shorter than the previous one (the footprint guard, logged five times on the 23:22-23:34
/// stage-2 pass) and could also re-append an id the player no longer owned (a phantom row).
/// Now, when the table the game hands the rebuild is one of the two weapon order templates,
/// every extended id whose bag byte is non-zero is seated into that template FIRST (the same
/// <see cref="TemplateSeat"/> policy the load edge uses), so the game's own rebuild lists it in
/// template order and sizes its own list; the re-append stays as a fallback but only for ids
/// the bag holds.
/// </summary>
internal sealed partial class OrderRebuildHook
{
    private readonly int _extendedCount;
    private readonly long _bagBase;   // LW-368 round 2: ExtendedInventory.BagCountBase, set at construction
    private long _seated;
    private long _seatRefusals;
    private bool _refusalWarned;

    /// <summary>Ids seated into a template ahead of the game's rebuild, cumulative.</summary>
    public long Seated => Interlocked.Read(ref _seated);
    /// <summary>Seatings a template could not take (no marker, no room, or a refused write).</summary>
    public long SeatRefusals => Interlocked.Read(ref _seatRefusals);
    private long _repaired;
    private readonly HashSet<long> _repairLogged = new();   // Info once per template per launch, Debug after
    /// <summary>Templates healed in place (doubled ids collapsed, a lost marker restored) ahead of a rebuild.</summary>
    public long Repaired => Interlocked.Read(ref _repaired);

    /// <summary>The extended ids the bag holds at least one of, ascending; empty when none or
    /// when the bag is unreadable.</summary>
    private List<int> OwnedExtendedIds()
    {
        var owned = new List<int>();
        for (int i = 0; i < _extendedCount; i++)
        {
            int id = ExtendedCatalog.FirstExtendedId + i;
            if (Owned(id)) owned.Add(id);
        }
        return owned;
    }

    /// <summary>True when the game's bag array holds at least one of <paramref name="id"/>; an
    /// unreadable byte counts as not owned (never seat or re-append on a guess).</summary>
    private bool Owned(int id) => _mem.TryRead(_bagBase + id, 1, out var b) && b[0] != 0;

    /// <summary>Seat the owned extended ids into <paramref name="table"/> when it is one of the
    /// two weapon order templates; any other table is left alone (the detour also serves lists
    /// whose templates this mod knows nothing about).</summary>
    private void SeatOwnedInto(nint table)
    {
        TemplateSeat.Region region = default;
        bool known = false;
        foreach (var r in TemplateSeat.WeaponRegions)
            if (r.Addr == (long)table) { region = r; known = true; break; }
        if (!known) return;
        // Round 8c: an empty owned list no longer returns early; the repair half of Plan must
        // still see the table (a doubled or marker-less template is damage whoever owns what).
        var owned = OwnedExtendedIds();
        if (!_mem.TryRead(region.Addr, region.CapacityWords * 2, out var bytes)) return;
        var seat = TemplateSeat.Plan(bytes, region.CapacityWords, owned);
        if (seat.Refusal != null) { Refused($"{region.Label} at 0x{region.Addr:X} could not take the owned new item(s) before the menu rebuild ({seat.Refusal})."); return; }
        if (!seat.Writes) return;
        if (!_mem.TryWrite(region.Addr + (long)seat.WordIndex * 2, seat.Bytes!)) { Refused($"{region.Label} at 0x{region.Addr:X} refused the seating write."); return; }
        if (seat.Repaired != null)
        {
            Interlocked.Increment(ref _repaired);
            bool first;
            lock (_repairLogged) first = _repairLogged.Add(region.Addr);
            string line = $"{region.Label} at 0x{region.Addr:X} was repaired ahead of the menu rebuild: {seat.Repaired}.";
            SafeLog(() => { if (first) ModLogger.Event(LogVerb.Engine, line); else ModLogger.Debug(LogVerb.Engine, line); });
            return;
        }
        int n = seat.Bytes!.Length / 2 - 1;
        Interlocked.Add(ref _seated, n);
        SafeLog(() => ModLogger.Debug(LogVerb.Engine, $"Seated {n} owned extended item id(s) into {region.Label} ahead of the menu rebuild (0x{region.Addr:X}, from word {seat.WordIndex})."));
    }

    private void Refused(string why)
    {
        Interlocked.Increment(ref _seatRefusals);
        if (_refusalWarned) { SafeLog(() => ModLogger.Debug(LogVerb.Engine, why)); return; }
        _refusalWarned = true;
        SafeLog(() => ModLogger.Warn(LogVerb.Engine, "Some owned new items may be missing from a menu until the next save loads: " + why));
    }
}
