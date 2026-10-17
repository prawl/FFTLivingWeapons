using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// When a weapon's design moves to a new item id, its kills move with it. The Terrastaff
/// used to sit in the Battle Axe's slot and now has its own id, so a save with 40 kills on the old
/// id shows 40 on the new one.
/// The pairing is data. An items.json row carries <c>migratedFrom</c>, the bake copies it into
/// meta.json (WeaponMeta.MigratedFrom), and this file reads it.
/// A count moves only while the old id has no meta entry of its own. Once a living weapon takes the
/// slot back, the old id keeps its tally.
/// The move deletes the old key, so a second load finds nothing. Collisions sum. No file I/O or
/// logging here, Engine persists (KillTally.Save, LegendStore.SaveIfDirty).
/// </summary>
internal static class TallyMigration
{
    /// <summary>Old id to new id for every move the meta calls for. If two entries claim the same
    /// old id, the lower new id wins.</summary>
    internal static Dictionary<int, int> Plan(IReadOnlyDictionary<int, WeaponMeta> meta)
    {
        var plan = new Dictionary<int, int>();
        foreach (var kv in meta)
        {
            int from = kv.Value?.MigratedFrom ?? 0;
            if (from <= 0 || from == kv.Key) continue;
            if (meta.ContainsKey(from)) continue;   // the old id is living again. It keeps its own
            if (!plan.TryGetValue(from, out int already) || kv.Key < already) plan[from] = kv.Key;
        }
        return plan;
    }

    /// <summary>Applies <paramref name="plan"/> to the kill map, summing on collision. Returns how
    /// many old ids moved.</summary>
    internal static int MoveKills(IReadOnlyDictionary<int, int> plan, Dictionary<int, int> kills)
    {
        int moved = 0;
        foreach (var kv in plan)
        {
            if (!kills.TryGetValue(kv.Key, out int count)) continue;
            kills.Remove(kv.Key);
            kills[kv.Value] = kills.TryGetValue(kv.Value, out int existing) ? existing + count : count;
            moved++;
        }
        return moved;
    }

    /// <summary>Applies <paramref name="plan"/> to the deed ledger. An empty destination takes the
    /// old record whole. Otherwise counts sum, Marks union, and the Phase 2 lists append. The
    /// destination keeps its own lastVictim/lastPainted. Returns how many old ids moved.</summary>
    internal static int MoveLegends(IReadOnlyDictionary<int, int> plan, Dictionary<int, WeaponLegend> legends)
    {
        int moved = 0;
        foreach (var kv in plan)
        {
            if (!legends.TryGetValue(kv.Key, out var from)) continue;
            legends.Remove(kv.Key);
            if (!legends.TryGetValue(kv.Value, out var into)) legends[kv.Value] = from;
            else Absorb(into, from);
            moved++;
        }
        return moved;
    }

    /// <summary>Folds <paramref name="from"/> into <paramref name="into"/> (see <see cref="MoveLegends"/>).</summary>
    private static void Absorb(WeaponLegend into, WeaponLegend from)
    {
        for (int i = 0; i < LegendStore.ArchetypeSlots; i++) into.Counts[i] += from.Counts[i];
        foreach (int mark in from.Marks)
            if (!into.Marks.Contains(mark)) into.Marks.Add(mark);
        into.Legends.AddRange(from.Legends);
        into.PendingAnnounce.AddRange(from.PendingAnnounce);
        if (into.LastVictimCls < 0 && from.LastVictimCls >= 0)
        {
            into.LastVictimNameId = from.LastVictimNameId;
            into.LastVictimJob = from.LastVictimJob;
            into.LastVictimCls = from.LastVictimCls;
        }
    }
}
