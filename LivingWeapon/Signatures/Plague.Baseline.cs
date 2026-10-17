using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>Per-slot poison baseline for the Plague latch. Last-seen poison bit, unit fingerprint,
/// and when the bit last rose at each band-slot address. Poison can land a tick before or after
/// the acted window, so the latch compares edge time to window time within a grace. A unit first
/// seen poisoned yields no edge; a fingerprint change resets the slot.</summary>
internal sealed class PlagueBaseline
{
    /// <summary>"No edge seen" sentinel, kept above long.MinValue so subtraction cannot overflow.</summary>
    public const long NoEdge = long.MinValue / 2;

    private readonly Dictionary<long, BaselineEntry> _baseline = new();

    /// <summary>Record the poison state for a slot, stamping the time on a false->true edge for the
    /// same unit. Call every tick before any latch decision.</summary>
    public void Update(long addr, (int mhp, int lvl, int br, int fa) fp, bool poisoned, long nowMs)
    {
        if (_baseline.TryGetValue(addr, out var prev) && prev.Fp.Equals(fp))
        {
            long edge = !prev.Poisoned && poisoned ? nowMs : prev.LastEdgeMs;
            _baseline[addr] = new BaselineEntry(fp, poisoned, edge);
            return;
        }
        // New slot or replaced unit. No prior, so no edge.
        _baseline[addr] = new BaselineEntry(fp, poisoned, NoEdge);
    }

    /// <summary>When the poison bit last rose for this unit, or <see cref="NoEdge"/> if never seen.</summary>
    public long LastEdgeMs(long addr, (int mhp, int lvl, int br, int fa) fp)
        => _baseline.TryGetValue(addr, out var e) && e.Fp.Equals(fp) ? e.LastEdgeMs : NoEdge;

    /// <summary>Clear all baseline entries (battle exit).</summary>
    public void ClearAll() => _baseline.Clear();

    private record struct BaselineEntry((int mhp, int lvl, int br, int fa) Fp, bool Poisoned, long LastEdgeMs);
}
