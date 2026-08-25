using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>LW-295 cycle B: the pure half of the icon-glow runtime -- no I/O, no Mem, given the
/// same inputs it always answers the same way. Three jobs: what tier does each weapon WANT
/// (<see cref="DesiredTiers"/>), which of those differ from what is currently applied
/// (<see cref="Diff"/>), and how many times does a base-tex needle occur in a byte buffer
/// (<see cref="FindNeedle"/>, classified by <see cref="ClassifyHitCount"/>). IconGlow.cs is the
/// stateful half that owns the manifest, the store, and the background splice.</summary>
internal static class IconGlowPolicy
{
    /// <summary>The three possible outcomes of searching modded.pac for one icon's deployed base
    /// tex bytes. Only <see cref="FoundOnce"/> is safe to manage: a needle that never occurs (some
    /// other mod's icon override already replaced it) or occurs more than once (a byte pattern
    /// this short collided) both degrade the icon to unmanaged rather than guess which offset is
    /// really it -- a wrong offset corrupts a 64MB file the game reads back.</summary>
    public enum NeedleVerdict { FoundOnce, NotFound, Ambiguous }

    /// <summary>Every id's current kill-tier per Tuning.TierOf, independent of who (if anyone)
    /// wields it -- unlike WeaponPalette's battle-sprite glow, the equip icon is a single asset
    /// per weapon id with no concept of "whose turn it is", so it reflects the shared tally alone.</summary>
    public static Dictionary<int, int> DesiredTiers(Dictionary<int, int> kills, IEnumerable<int> ids)
    {
        var desired = new Dictionary<int, int>();
        foreach (var id in ids) desired[id] = Tuning.TierOf(kills, id);
        return desired;
    }

    /// <summary>Ids whose desired tier differs from what is currently applied (an id absent from
    /// <paramref name="applied"/> counts as tier 0, the guaranteed post-launch state -- P6: the
    /// pac is rebuilt from loose files every launch). Empty when the two already agree.</summary>
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

    public static NeedleVerdict ClassifyHitCount(int hitCount) => hitCount switch
    {
        0 => NeedleVerdict.NotFound,
        1 => NeedleVerdict.FoundOnce,
        _ => NeedleVerdict.Ambiguous,
    };

    /// <summary>Counts occurrences of <paramref name="needle"/> in <paramref name="haystack"/>,
    /// capped at 2 (Ambiguous only ever needs "more than one", never the true count, so a common
    /// byte run never forces scanning the rest of a 64MB buffer). <paramref name="firstOffset"/>
    /// is the offset of the first hit, meaningful only when exactly one hit is found -- that is
    /// the offset IconGlow stores for the whole launch and splices at without ever re-searching
    /// (U10: after the first splice the base needle no longer exists at that offset).</summary>
    public static int FindNeedle(byte[] haystack, byte[] needle, out long firstOffset)
    {
        firstOffset = -1;
        if (needle.Length == 0 || haystack.Length < needle.Length) return 0;

        int hits = 0;
        int start = 0;
        while (start <= haystack.Length - needle.Length)
        {
            int idx = new ReadOnlySpan<byte>(haystack, start, haystack.Length - start).IndexOf(needle);
            if (idx < 0) break;
            long offset = start + idx;
            if (hits == 0) firstOffset = offset;
            hits++;
            if (hits >= 2) break;
            start += idx + 1;
        }
        return hits;
    }
}
