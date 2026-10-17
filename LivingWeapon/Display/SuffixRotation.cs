using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-ID suffix coverage for the rotation slice, kept as a set rather than a shared cursor.
/// A cursor clamped to each chunk's id count let a small render buffer reset the one the
/// 120-card master text was walking, so tail ids never got painted (live: bows missed their +3).
/// </summary>
internal sealed class SuffixRotation
{
    internal const int RotationSlice = 8;

    private readonly HashSet<int> _covered = new();

    /// <summary>Picks up to RotationSlice non-target ids from this chunk that have not had a
    /// suffix search this cycle. When all of them are covered they are released and a new cycle starts.</summary>
    public IReadOnlyList<int> Take(IEnumerable<int> ids, IReadOnlySet<int> targets)
    {
        // Targets are already in suffixIds.
        var nonTargets = new List<int>();
        foreach (int id in ids)
            if (!targets.Contains(id)) nonTargets.Add(id);
        if (nonTargets.Count == 0) return nonTargets;

        var take = new List<int>(RotationSlice);
        foreach (int id in nonTargets)
        {
            if (_covered.Contains(id)) continue;
            take.Add(id);
            if (take.Count == RotationSlice) break;
        }
        if (take.Count == 0)
        {
            // Cycle done for this chunk's ids, release them and start the next round.
            foreach (int id in nonTargets) _covered.Remove(id);
            foreach (int id in nonTargets)
            {
                take.Add(id);
                if (take.Count == RotationSlice) break;
            }
        }
        foreach (int id in take) _covered.Add(id);
        return take;
    }
}
