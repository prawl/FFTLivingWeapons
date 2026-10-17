using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>The pure half of the icon-glow
/// runtime. Works out the tier each weapon wants (<see cref="DesiredTiers"/>) and which of those
/// differ from what is applied (<see cref="Diff"/>). IconGlow.cs holds the state and
/// IconGlow.Apply.cs does the background judge and write.</summary>
internal static class IconGlowPolicy
{
    /// <summary>Current kill tier per Tuning.TierOf for every id. The equip icon is one asset
    /// per weapon id, so unlike WeaponPalette's battle glow it ignores who wields it.</summary>
    public static Dictionary<int, int> DesiredTiers(Dictionary<int, int> kills, IEnumerable<int> ids)
    {
        var desired = new Dictionary<int, int>();
        foreach (var id in ids) desired[id] = Tuning.TierOf(kills, id);
        return desired;
    }

    /// <summary>Ids whose desired tier differs from the applied one. An id missing from
    /// <paramref name="applied"/> counts as tier 0 until IconGlow.Apply.cs's JudgeId reads the
    /// deployed tex and corrects <c>_applied</c>, so an already-tiered tex produces no write.</summary>
    public static Dictionary<int, int> Diff(Dictionary<int, int> applied, Dictionary<int, int> desired)
    {
        var changed = new Dictionary<int, int>();
        foreach (var kv in desired)
        {
            int have = applied.TryGetValue(kv.Key, out var tier) ? tier : 0;
            if (have != kv.Value) changed[kv.Key] = kv.Value;
        }
        return changed;
    }
}
