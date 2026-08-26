using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>LW-295 cycle B (needle machinery retired LW-336): the pure half of the icon-glow
/// runtime -- no I/O, no Mem, given the same inputs it always answers the same way. Two jobs:
/// what tier does each weapon WANT (<see cref="DesiredTiers"/>), and which of those differ from
/// what is currently applied (<see cref="Diff"/>). IconGlow.cs is the stateful half that owns the
/// manifest and the store; IconGlow.Apply.cs owns the background judge + write.</summary>
internal static class IconGlowPolicy
{
    /// <summary>Every id's current kill-tier per Tuning.TierOf, independent of who (if anyone)
    /// wields it -- unlike WeaponPalette's battle-sprite glow, the equip icon is a single asset
    /// per weapon id with no concept of "whose turn it is", so it reflects the shared tally alone.</summary>
    public static Dictionary<int, int> DesiredTiers(Dictionary<int, int> kills, IEnumerable<int> ids)
    {
        var desired = new Dictionary<int, int>();
        foreach (var id in ids) desired[id] = Tuning.TierOf(kills, id);
        return desired;
    }

    /// <summary>Ids whose desired tier differs from what is currently applied. An id absent from
    /// <paramref name="applied"/> counts as tier 0 only PROVISIONALLY: this runtime has never
    /// touched it yet, so the diff assumes the guaranteed post-launch floor and lets the first
    /// background apply's judge (IconGlow.Apply.cs's JudgeId) correct <c>_applied</c> from the
    /// real deployed tex before any write happens. That judge -- not a "the pac rebuilds every
    /// launch" assumption -- is what keeps this diff truthful; a deployed tex that already holds
    /// the desired tier's exact bytes (e.g. a prior session, or a pre-tiered install) gets seeded
    /// straight to that tier and produces no write at all. Empty when the two already agree.</summary>
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
