using System;
using System.Collections.Generic;
using System.Threading;

namespace LivingWeapon;

/// <summary>
/// Writes the new items the party owns into the weapon order charts just before the game rebuilds a menu.
/// An item bought this session is not in the saved chart, so the rebuild drops it. Adding it back afterwards
/// failed whenever the rebuilt list came out shorter (five times in one pass) and could
/// bring back an item no longer owned. Seated first through <see cref="TemplateSeat"/>, as the load does, the
/// game sizes its own list, and the add back stays only as a fallback for owned items.
/// </summary>
internal sealed partial class OrderRebuildHook
{
    private readonly int _extendedCount;
    private readonly long _bagBase;
    private readonly Func<TemplateSeat.Region[]>? _regions;   // asked each call because the charts move when the relocation arms
    private long _seated;
    private long _seatRefusals;
    // The first refusal warns and later ones go to Debug, so a full chart cannot flood the log.
    private bool _refusalWarned;

    /// <summary>How many ids were seated ahead of a rebuild, all session.</summary>
    public long Seated => Interlocked.Read(ref _seated);
    /// <summary>Seatings a chart could not take (no marker, no room, or a refused write).</summary>
    public long SeatRefusals => Interlocked.Read(ref _seatRefusals);
    private long _repaired;
    private readonly HashSet<long> _repairLogged = new();   // each chart's first repair logs at Info, later ones at Debug
    /// <summary>Charts fixed in place ahead of a rebuild, a doubled id removed or a lost marker put back.</summary>
    public long Repaired => Interlocked.Read(ref _repaired);

    /// <summary>New items the bag holds at least one of, lowest id first.</summary>
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

    /// <summary>A bag that cannot be read counts as not owned.</summary>
    private bool Owned(int id) => _mem.TryRead(_bagBase + id, 1, out var b) && b[0] != 0;

    private void SeatOwnedInto(nint table)
    {
        var regions = _regions?.Invoke() ?? TemplateSeat.WeaponRegions;
        TemplateSeat.Region region = default;
        bool known = false;
        foreach (var r in regions)
            if (r.Addr == (long)table) { region = r; known = true; break; }
        // The rebuild runs for every menu list and only the two weapon charts are ours to touch.
        if (!known) return;
        // Keep going with nothing owned, because a doubled id or a lost end marker still needs fixing.
        var owned = OwnedExtendedIds();
        if (!_mem.TryRead(region.Addr, region.CapacityWords * 2, out var bytes)) return;
        // An id past the armed range belongs to an item this build no longer ships, so the repair drops it.
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
