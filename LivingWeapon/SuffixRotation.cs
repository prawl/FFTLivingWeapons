using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Per-ID suffix coverage for the rotation slice. A SET, not a cursor: the old shared
/// cursor was clamped to each chunk's id count, so a 2-card render buffer rescanned every
/// 250ms kept resetting the position the 120-card master text was walking and tail ids
/// were never suffix-painted (live: bows never showed their +3). Ids release for a new
/// cycle once a chunk's set is exhausted, so fresh buffers wait at most one cycle.
/// </summary>
internal sealed class SuffixRotation
{
    internal const int RotationSlice = 8;

    private readonly HashSet<int> _covered = new();

    /// <summary>Pick up to RotationSlice non-target ids from this chunk's hit set that have
    /// not had a suffix search this coverage cycle. Coverage is per-ID (a set), never a shared
    /// cursor: a cursor clamped to each chunk's id count let a small render-buffer chunk reset
    /// the position the big master-text chunk was walking, starving tail ids forever (live:
    /// the bows never got their +3). When every id this chunk offers is already covered, its
    /// ids are released and a new cycle starts -- so a fresh render buffer of an already-covered
    /// id waits at most one full cycle, and every id provably gets its turn.</summary>
    public IReadOnlyList<int> Take(IEnumerable<int> ids, IReadOnlySet<int> targets)
    {
        // Keep only non-target ids (targets are already in suffixIds unconditionally).
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
            // Cycle complete for this chunk's ids: release them and start the next round.
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
