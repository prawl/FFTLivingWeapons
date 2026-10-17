using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Two-lane per-pass paint verdict ledger (LW-257 commit 1), keyed by SlotAddr rather than weapon id.
/// Per-id aggregation hid a missing equip-card copy while Display.PoolPaint.cs CoversAllMeta() still
/// reported complete, since CardSites.cs sizes pools at about 5.8 copies per id.
///
/// Notable lane (<see cref="Entries"/>): every outcome except AlreadyEqual, capped at <see cref="MaxEntries"/>,
/// extras dropped. Display.Flight.cs EmitVerdict walks this lane. A steady-state pass is ~836-2000 sites,
/// nearly all AlreadyEqual, which filled a shared cap with the same prefix every pass.
///
/// Settled lane (<see cref="Settled"/>): unbounded (Id, SlotAddr) set of every Wrote and AlreadyEqual,
/// queried by id and predicate, never enumerated. It backs the LW-257 commit 2 pending-set clear.
/// </summary>
internal sealed class CardVerdict
{
    internal readonly record struct Entry(int Id, long SlotAddr, PaintOutcome Outcome, int Observed, bool Evicted);

    /// <summary>Headroom for the notable lane without holding a full PaintAll pass (see the class doc).</summary>
    internal const int MaxEntries = 256;

    private readonly List<Entry> _notable = new();
    private readonly HashSet<(int Id, long SlotAddr)> _settled = new();

    /// <summary>The notable lane only. What EmitVerdict walks.</summary>
    public IReadOnlyList<Entry> Entries => _notable;

    /// <summary>Record one site's outcome for this pass. AlreadyEqual goes to the settled lane only,
    /// Wrote goes to both lanes, everything else to the notable lane only. <paramref name="evicted"/> is true
    /// only when this call actually removed the site from the CardSites cache (PaintAll does, the targeted
    /// Paint for new sites does not). A strike short of Tuning.CardEvictStrikes is not an eviction.
    ///
    /// When the notable lane is full, an incoming eviction replaces the first non-evicted entry. A
    /// first-light discovery pass can note ~836 Wrote entries in one Tick (Display.cs OnChunk, same verdict,
    /// before PaintAllTapped clears it), which would otherwise fill the lane before PaintAll finds any eviction.
    /// If the lane is all evictions, the newer one is dropped.
    ///
    /// Costs of the replacement: it scans from index 0, so O(n) at worst (cheap at 256). And the
    /// replaced slot keeps its list position, so <see cref="Entries"/> is not insertion-ordered after a
    /// replacement. EmitVerdict (Display.Flight.cs) walks by outcome and Evicted, not position, so it is unaffected.</summary>
    public void Note(int id, long slotAddr, PaintOutcome outcome, int observed, bool evicted = false)
    {
        if (outcome == PaintOutcome.Wrote || outcome == PaintOutcome.AlreadyEqual)
            _settled.Add((id, slotAddr));

        if (outcome == PaintOutcome.AlreadyEqual) return;   // settled lane only

        var entry = new Entry(id, slotAddr, outcome, observed, evicted);
        if (_notable.Count < MaxEntries) { _notable.Add(entry); return; }

        if (!evicted) return;   // lane full; a routine entry does not displace anything
        for (int i = 0; i < _notable.Count; i++)
        {
            if (_notable[i].Evicted) continue;
            _notable[i] = entry;
            return;
        }
        // every entry is already an eviction: drop, like any other over-cap entry.
    }

    /// <summary>True if <paramref name="id"/> settled (Wrote or AlreadyEqual) at any site this pass
    /// whose address satisfies <paramref name="addrPredicate"/>. Used by the LW-257 commit 2 pending-set clear.</summary>
    public bool Settled(int id, Func<long, bool> addrPredicate)
    {
        foreach (var (sid, addr) in _settled)
            if (sid == id && addrPredicate(addr)) return true;
        return false;
    }

    /// <summary>Reset both lanes for the next pass. The caller owns the pass boundary.</summary>
    public void Clear() { _notable.Clear(); _settled.Clear(); }
}
