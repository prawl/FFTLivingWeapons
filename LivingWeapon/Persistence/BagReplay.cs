using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// LW-351: the ONE bag-replay policy the extended inventory's two load paths share.
///
/// WHY TWO PATHS: the load-apply routine copies the save file's bag over the game's own count
/// array, and that file carries counts for ids 0..260 only, so the instant a load returns every
/// extended id reads zero. Every menu that asks whether the player owns one (the qualifying gate
/// is bag count plus equipped count) would answer no until the mod put the counts back, and the
/// Engine tick alone would leave that window open for up to 33 ms. The replay therefore runs
/// inside the load detour (SaveEdgeHooks.AfterApply), on the game's own thread, right after the
/// original returns; the Engine tick then runs the SAME resolution as an idempotent fallback, and
/// owns the one log line. One policy called from both places, so the two orders cannot drift
/// apart.
///
/// ROUND 5 CORRECTED ROUND 4's PREMISE (2026-08-30): the earlier note here said the game rebuilds
/// its menu order templates during the load from the bag, which the closing disassembly disproved:
/// the load-apply RESTORES both templates byte-for-byte out of the save struct and the
/// serializer writes them back, so they are save state and no load-time rebuild reads the bag at
/// all. Putting the counts back early is still necessary and still lands in the right place; it
/// was never sufficient on its own, which is why the same detour moment also calls
/// <see cref="TemplateSeat"/> with <see cref="OwnedIds"/>.
///
/// The resolution order is exactly what the tick has done since LW-353: the counts recorded for
/// that save's key, else the schema-1 counts once (the pre-key migration), else the data's
/// first-copy seed. <see cref="Resolve"/> is the only place that spends that one-shot migration,
/// and since LW-351 stage 2 it records the migrated counts under the key it resolved them for,
/// so a second resolve of the same key (the game's second load routine, the tick, the next
/// launch) answers with the same counts instead of the seed. The detour still hands its
/// <see cref="Plan"/> forward to the tick so the tick does not resolve the key a second time.
/// </summary>
internal static class BagReplay
{
    /// <summary>What one load edge should place, plus the phrase the tick's log line uses.</summary>
    internal sealed class Plan
    {
        public Plan(string key, IReadOnlyDictionary<int, int> counts, string source)
        {
            Key = key;
            Counts = counts;
            Source = source;
        }

        /// <summary>The save key this plan was resolved for (the tick only re-uses a plan whose
        /// key matches the load edge it is draining).</summary>
        public string Key { get; }

        public IReadOnlyDictionary<int, int> Counts { get; }

        /// <summary>Where the counts came from, in the words the log line says.</summary>
        public string Source { get; }
    }

    /// <summary>Resolve the counts for a loaded save's key. This SPENDS the schema-1 one-shot when
    /// that is the answer (and persists the fact), so it must run once per load edge, not once per
    /// path.</summary>
    public static Plan Resolve(ExtendedBagSidecar sidecar, IReadOnlyList<ExtendedItemDef> items, string key)
    {
        if (sidecar.TryGetSave(key, out var known)) return new Plan(key, known, "its own recorded counts");
        var legacy = sidecar.TakeLegacy();
        if (legacy != null)
        {
            // P4-F1 (LW-351 stage 2): record the migrated counts UNDER THIS KEY, not merely
            // "spent". Both of the game's load routines are hooked, so one logical load can
            // resolve the same key twice; with a bare persist the second resolve found the key
            // unknown and re-seeded it, throwing the migrated counts away between one routine
            // and the next. RecordSave persists, so the one-shot is still spent exactly once.
            sidecar.RecordSave(key, legacy);
            return new Plan(key, legacy, "the pre-LW-353 counts (one-time migration)");
        }
        return new Plan(key, items.ToDictionary(i => i.Id, i => i.SeedCount),
            "the first-copy seed (this save was never seen with the mod running)");
    }

    /// <summary>The count this plan gives one item; a gap in the plan falls back to its seed.</summary>
    public static int CountFor(Plan plan, ExtendedItemDef item)
        => plan.Counts.TryGetValue(item.Id, out int c) ? c : item.SeedCount;

    /// <summary>Write the plan into the bag: one guarded byte per extended id, nothing else.
    /// <paramref name="onRefused"/> is null on the detour path, which must not log on the game's
    /// thread; the tick passes its warning.</summary>
    public static void Apply(ICodePatcher patcher, IReadOnlyList<ExtendedItemDef> items, Plan plan,
        Action<ExtendedItemDef, int>? onRefused = null)
    {
        foreach (var item in items)
        {
            int n = CountFor(plan, item);
            if (!patcher.TryWrite(Offsets.BagCountArray + item.Id, new[] { (byte)n })) onRefused?.Invoke(item, n);
        }
    }

    /// <summary>LW-351 fix round 5: the extended ids this plan leaves the player owning at least
    /// one of, ascending. Those are the ids <see cref="TemplateSeat"/> seats in the menu order
    /// templates, because a template lists items the player owns and nothing else.</summary>
    public static IReadOnlyList<int> OwnedIds(Plan plan, IReadOnlyList<ExtendedItemDef> items)
        => items.Where(i => CountFor(plan, i) > 0).Select(i => i.Id).OrderBy(id => id).ToList();

    /// <summary>"Terrastaff x3, Ravager x1", the tail of the tick's log line.</summary>
    public static string Describe(Plan plan, IReadOnlyList<ExtendedItemDef> items)
        => string.Join(", ", items.Select(i => $"{i.Name} x{CountFor(plan, i)}"));
}
