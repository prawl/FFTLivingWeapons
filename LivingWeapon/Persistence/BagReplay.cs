using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>
/// LW-351 bag-replay policy shared by the extended inventory's two load paths. The save file carries counts for ids
/// 0..260 only, so right after a load every extended id reads zero until the mod restores them. The replay runs in the
/// load detour (SaveEdgeHooks.AfterApply) on the game's thread; the Engine tick runs the same resolution as an idempotent
/// fallback and owns the log line. Round 5 (2026-08-30) found that the load-apply restores both menu order templates
/// from the save struct byte for byte, so they are save state, and the detour therefore also calls <see cref="TemplateSeat"/>
/// with <see cref="OwnedIds"/>. Resolution order since LW-353 is the counts recorded for the save's key, else the schema-1
/// counts once (pre-key migration), else the data's first-copy seed. <see cref="Resolve"/> spends that migration and, since
/// LW-351 stage 2, records the result under the key so a second resolve answers the same. The detour hands its
/// <see cref="Plan"/> to the tick so the key is not resolved twice.
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

        /// <summary>The save key this plan was resolved for; the tick reuses a plan only when it matches the load edge.</summary>
        public string Key { get; }

        public IReadOnlyDictionary<int, int> Counts { get; }

        /// <summary>Where the counts came from, in the words the log line says.</summary>
        public string Source { get; }
    }

    /// <summary>Resolve the counts for a loaded save's key. Spends the schema-1 one-shot when that is the answer, so run it
    /// once per load edge, not once per path.</summary>
    public static Plan Resolve(ExtendedBagSidecar sidecar, IReadOnlyList<ExtendedItemDef> items, string key)
    {
        if (sidecar.TryGetSave(key, out var known)) return new Plan(key, known, "its own recorded counts");
        var legacy = sidecar.TakeLegacy();
        if (legacy != null)
        {
            // P4-F1 (LW-351 stage 2) record the migrated counts under this key. Both load routines are hooked, so one load can
            // resolve a key twice, and a bare persist let the second resolve re-seed it. RecordSave persists, so the one-shot is
            // still spent once.
            sidecar.RecordSave(key, legacy);
            return new Plan(key, legacy, "the pre-LW-353 counts (one-time migration)");
        }
        return new Plan(key, items.ToDictionary(i => i.Id, i => i.SeedCount),
            "the first-copy seed (this save was never seen with the mod running)");
    }

    /// <summary>The count this plan gives one item; a gap in the plan falls back to its seed.</summary>
    public static int CountFor(Plan plan, ExtendedItemDef item)
        => plan.Counts.TryGetValue(item.Id, out int c) ? c : item.SeedCount;

    /// <summary>Write the plan into the bag, one guarded byte per extended id. <paramref name="bagBase"/> is
    /// <see cref="ExtendedInventory.BagCountBase"/> (LW-368 round 2, the relocated page once armed, else the vanilla block),
    /// passed explicitly so a stale base cannot slip in. <paramref name="onRefused"/> is null on the detour path, which
    /// must not log on the game's thread; the tick passes its warning.</summary>
    public static void Apply(ICodePatcher patcher, IReadOnlyList<ExtendedItemDef> items, Plan plan, long bagBase,
        Action<ExtendedItemDef, int>? onRefused = null)
    {
        foreach (var item in items)
        {
            int n = CountFor(plan, item);
            if (!patcher.TryWrite(bagBase + item.Id, new[] { (byte)n })) onRefused?.Invoke(item, n);
        }
    }

    /// <summary>LW-351 fix round 5, the extended ids this plan leaves the player owning, ascending. These are what
    /// <see cref="TemplateSeat"/> seats in the menu order templates.</summary>
    public static IReadOnlyList<int> OwnedIds(Plan plan, IReadOnlyList<ExtendedItemDef> items)
        => items.Where(i => CountFor(plan, i) > 0).Select(i => i.Id).OrderBy(id => id).ToList();

    /// <summary>"Terrastaff x3, Ravager x1", the tail of the tick's log line.</summary>
    public static string Describe(Plan plan, IReadOnlyList<ExtendedItemDef> items)
        => string.Join(", ", items.Select(i => $"{i.Name} x{CountFor(plan, i)}"));
}
