using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-351: when a weapon's DESIGN moves to a different item id, the kills the player earned with
/// it move too. In plain language: the Terrastaff used to live in the Battle Axe's slot; now the
/// Battle Axe has its slot back and the Terrastaff has a new id of its own, and a save that had
/// counted 40 kills on the old id must show those 40 on the new one, not start over.
///
/// The pairing is DATA, not code: an items.json row carries <c>migratedFrom</c>, the bake copies
/// it into meta.json (WeaponMeta.MigratedFrom), and this file only reads it. Nothing here knows
/// the word "Terrastaff", so stage 2's six further pairs need no change to this file.
///
/// THE RULE, and the one case it must NOT fire on: a count or deed at an old id moves to the new
/// id only while the old id has NO meta entry of its own. The moment something living moves back
/// into that slot (LW-361 redesigns the restored axes and flails), the old id owns its own tally
/// again and a migration would steal it. That negative is the load-bearing test.
///
/// One-shot by construction rather than by a flag: the move DELETES the old key, so the second
/// load finds nothing to move. Sums on collision (both ids present) so nothing is ever dropped.
/// Pure over its inputs -- no file I/O, no logging -- so Engine owns the persist (KillTally.Save
/// / LegendStore.SaveIfDirty, both of which take the previous generation to .bak).
/// </summary>
internal static class TallyMigration
{
    /// <summary>old id -&gt; new id for every move this meta calls for. An entry qualifies when it
    /// names a <c>migratedFrom</c> id that is not itself an emitted meta entry. A second entry
    /// claiming the SAME old id would be an authoring error (two designs cannot both inherit one
    /// slot's kills); the lower new id wins deterministically rather than dictionary order.</summary>
    internal static Dictionary<int, int> Plan(IReadOnlyDictionary<int, WeaponMeta> meta)
    {
        var plan = new Dictionary<int, int>();
        foreach (var kv in meta)
        {
            int from = kv.Value?.MigratedFrom ?? 0;
            if (from <= 0 || from == kv.Key) continue;
            if (meta.ContainsKey(from)) continue;   // the old id is living again: it keeps its own
            if (!plan.TryGetValue(from, out int already) || kv.Key < already) plan[from] = kv.Key;
        }
        return plan;
    }

    /// <summary>Apply <paramref name="plan"/> to the shared kill map, summing on collision.
    /// Returns how many old ids actually moved (0 = nothing to persist).</summary>
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

    /// <summary>Apply <paramref name="plan"/> to the deed ledger. A destination with no record of
    /// its own takes the old one whole; a destination that already has one absorbs the old one
    /// (per-archetype counts sum, Marks union in earn order, the stored Phase 2 lists append).
    /// The destination's own lastVictim/lastPainted win: those describe what the NEW id did most
    /// recently, and inventing an older "most recent" would be a lie the card then paints.
    /// Returns how many old ids actually moved.</summary>
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

    /// <summary>Fold <paramref name="from"/>'s record into <paramref name="into"/> (see
    /// <see cref="MoveLegends"/> for which side wins what).</summary>
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
