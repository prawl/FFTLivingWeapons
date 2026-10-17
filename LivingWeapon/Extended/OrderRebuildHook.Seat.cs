using System;
using System.Collections.Generic;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// LW-351 fix round 7 (2026-08-30). Seats owned extended ids into the weapon order templates before
/// the game rebuilds its menus. A new item bought this session is not in the saved order list, so
/// the rebuild dropped it, and the old after-the-fact re-append failed when the rebuilt list was
/// shorter (the footprint guard, five times in the 23:22-23:34 stage-2 pass) or re-added an unowned
/// id. Seating first via <see cref="TemplateSeat"/>, as the load edge does, lets the game size its
/// own list. The re-append stays as a fallback for owned ids only.
/// </summary>
internal sealed partial class OrderRebuildHook
{
    private readonly int _extendedCount;
    private readonly long _bagBase;   // LW-368 round 2: ExtendedInventory.BagCountBase, set at construction
    private readonly Func<TemplateSeat.Region[]>? _regions;   // ExtendedInventory.TemplateRegions, set at construction
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

    /// <summary>Extended ids the bag holds at least one of, ascending; empty if none or unreadable.</summary>
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

    /// <summary>True when the bag holds at least one of <paramref name="id"/>; unreadable counts as not owned.</summary>
    private bool Owned(int id) => _mem.TryRead(_bagBase + id, 1, out var b) && b[0] != 0;

    /// <summary>Seat owned extended ids into <paramref name="table"/> if it is a weapon order template;
    /// other tables are left alone.</summary>
    private void SeatOwnedInto(nint table)
    {
        var regions = _regions?.Invoke() ?? TemplateSeat.WeaponRegions;
        TemplateSeat.Region region = default;
        bool known = false;
        foreach (var r in regions)
            if (r.Addr == (long)table) { region = r; known = true; break; }
        if (!known) return;
        // Round 8c, no early return on an empty owned list since Plan still repairs a doubled or marker-less template.
        var owned = OwnedExtendedIds();
        if (!_mem.TryRead(region.Addr, region.CapacityWords * 2, out var bytes)) return;
        // LW-367 ids at or past the armed range are stale and get dropped by the repair.
        var seat = TemplateSeat.Plan(bytes, region.CapacityWords, owned, ExtendedCatalog.FirstExtendedId + _extendedCount);
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
