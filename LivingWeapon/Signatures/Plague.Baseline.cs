using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Per-slot poison baseline for the Plague latch. Tracks the last-seen poison-bit
/// state, the unit fingerprint, and WHEN the bit last rose (false->true) at each band-slot
/// address; updated every tick regardless of the acted window. The timestamp is what makes the
/// latch tolerant of engine timing: poison is applied during attack resolution, which can land
/// a tick before the acted window is observed open or after it closes, so the latch matches
/// edge-time against window-time within a grace instead of requiring exact overlap.
/// A unit first seen already poisoned never yields an edge (pre-existing poison is excluded);
/// a fingerprint change (unit replaced) resets the slot.</summary>
internal sealed class PlagueBaseline
{
    /// <summary>"No edge ever observed" sentinel: far enough below zero that grace arithmetic
    /// can never reach it, far enough above long.MinValue that subtraction cannot overflow.</summary>
    public const long NoEdge = long.MinValue / 2;

    private readonly Dictionary<long, BaselineEntry> _baseline = new();

    /// <summary>Record the current poison state for a band slot, stamping the edge time when the
    /// bit transitions false->true with a valid same-unit prior. Call every tick for every valid
    /// band entry, before any latch decision.</summary>
    public void Update(long addr, (int mhp, int lvl, int br, int fa) fp, bool poisoned, long nowMs)
    {
        if (_baseline.TryGetValue(addr, out var prev) && prev.Fp.Equals(fp))
        {
            long edge = !prev.Poisoned && poisoned ? nowMs : prev.LastEdgeMs;
            _baseline[addr] = new BaselineEntry(fp, poisoned, edge);
            return;
        }
        // New slot or replaced unit: no valid prior, so no edge -- conservative by design.
        _baseline[addr] = new BaselineEntry(fp, poisoned, NoEdge);
    }

    /// <summary>When the poison bit last rose at this slot for this unit, or <see cref="NoEdge"/>
    /// when never observed (slot unknown, unit replaced, or first seen already poisoned).</summary>
    public long LastEdgeMs(long addr, (int mhp, int lvl, int br, int fa) fp)
        => _baseline.TryGetValue(addr, out var e) && e.Fp.Equals(fp) ? e.LastEdgeMs : NoEdge;

    /// <summary>Clear all baseline entries (battle exit).</summary>
    public void ClearAll() => _baseline.Clear();

    private record struct BaselineEntry((int mhp, int lvl, int br, int fa) Fp, bool Poisoned, long LastEdgeMs);
}
