using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Two-lane per-pass paint verdict ledger (LW-257 commit 1). Address-keyed, deliberately NOT
/// aggregated per weapon id -- per-id aggregation is exactly the blindness that let the equip
/// card's own on-screen copy go missing while Display.PoolPaint.cs's CoversAllMeta() still
/// reported coverage "complete" the whole time: CardSites.cs's own sizing comment puts pool
/// copies at about 5.8 per weapon id, so losing one still leaves that per-id presence check
/// satisfied. Keying by SlotAddr instead means the specific copy that went dark is visible in the
/// ledger, not averaged away.
///
/// NOTABLE lane (<see cref="Entries"/>): every outcome except AlreadyEqual -- Wrote,
/// AnchorMismatch, AnchorUnreadable, SlotUnreadable, SlotShapeRefused, NotWritable -- capped at
/// <see cref="MaxEntries"/> and silently dropped past the cap. This is the ONLY lane
/// Display.Flight.cs's EmitVerdict walks. Bounded this way ON PURPOSE, corrected from a v1 that
/// Noted every outcome including AlreadyEqual: a steady-state pass is ~836-2000 sites, nearly all
/// AlreadyEqual (the whole cache reads correct and nothing needs to change), and List.Remove/Add
/// preserve order, so a single shared cap filled by AlreadyEqual first meant entries 0..255 were a
/// FIXED prefix every single pass -- every interesting outcome past that prefix was dropped,
/// deterministically, forever, on every real cache the size PruneEveryRefusals/MaxSites already
/// assume. Excluding AlreadyEqual from this lane entirely is what makes the cap mean something
/// again: it now only ever fills with outcomes that are actually worth a diagnostic look.
///
/// SETTLED lane (<see cref="Settled"/>): an unbounded (Id, SlotAddr) set recording every Wrote
/// AND AlreadyEqual -- the two "this site currently shows the right value" outcomes. NEVER
/// consumes the notable cap and is never walked/enumerated, only queried by id+predicate. This is
/// the positive-proof half the LW-257 spec's commit 2 pending-set clear predicate needs ("did this
/// id settle at a site inside a located pool region"), and AlreadyEqual is exactly the
/// steady-state signal that proves it -- it cannot be dropped just because it is common, so it
/// gets a lane the notable cap can never starve. Commit 1 only exposes this shape; nothing in
/// commit 1 calls <see cref="Settled"/>.
/// </summary>
internal sealed class CardVerdict
{
    internal readonly record struct Entry(int Id, long SlotAddr, PaintOutcome Outcome, int Observed, bool Evicted);

    /// <summary>Generous diagnostic headroom for the notable lane without ever holding a full
    /// PaintAll pass in memory (see the class doc for the ~1400-2000 site sizing this is bounded
    /// against).</summary>
    internal const int MaxEntries = 256;

    private readonly List<Entry> _notable = new();
    private readonly HashSet<(int Id, long SlotAddr)> _settled = new();

    /// <summary>The notable lane only -- see the class doc. What EmitVerdict walks.</summary>
    public IReadOnlyList<Entry> Entries => _notable;

    /// <summary>Record one site's outcome for this pass. AlreadyEqual routes to the settled lane
    /// ONLY (never counted against MaxEntries, never appears in <see cref="Entries"/>); Wrote
    /// routes to BOTH lanes (it is notable AND a settled proof); every other outcome is notable
    /// only. <paramref name="evicted"/> must be true only on the pass where this site actually
    /// left the CardSites cache as a direct result of THIS call (CardSites.PaintSiteWithResult's
    /// PaintResult.Evict, and only when the caller actually removes it -- PaintAll does, the
    /// targeted Paint used for freshly-discovered sites does not) -- a strike that has not yet
    /// reached Tuning.CardEvictStrikes is NOT evicted even though its outcome is
    /// AnchorUnreadable, and must be recorded with evicted:false.
    ///
    /// ADMISSION PRIORITY (round-4 review, item 1): when the notable lane is already full, an
    /// incoming EVICTED entry bumps the first non-evicted entry already there instead of being
    /// dropped. This is load-bearing, not cosmetic: a first-light discovery pass can register
    /// ~836 Wrote entries in one Tick (Display.cs's OnChunk, which shares this SAME verdict and
    /// runs before the next PaintAllTapped clears it), which alone fills the 256-entry lane with
    /// routine paints before PaintAll's own pass -- the one that finds every eviction -- ever gets
    /// a turn. An eviction is the rare, expensive-to-lose signal (a strike-cap or a genuine
    /// buffer-reuse mismatch); a Wrote/refusal entry arriving first must never be allowed to
    /// permanently starve it just by getting here first. A lane that is somehow already 100%
    /// evictions has nothing lower-priority left to bump, so a newer eviction is dropped like
    /// anything else past the cap -- an acceptable edge the diagnostic-not-transcript contract
    /// (class doc) already accepts.
    ///
    /// Two honest costs of the bump, neither load-bearing enough to change the algorithm (LW-259
    /// doc pass): the bump loop is O(n) in the worst case -- it scans from index 0 looking for the
    /// first non-evicted slot, so a lane already dense with evictions near the front pays a longer
    /// scan on every further eviction admitted this pass; at MaxEntries=256 that is still cheap
    /// (a plain array scan, no allocation), just not O(1) like the normal Add path above it. And a
    /// bump means <see cref="Entries"/> is NOT reliably insertion-ordered the moment any bump has
    /// happened this pass: the bumped slot keeps its ORIGINAL list position, now holding a LATER
    /// entry, so a reader walking Entries front-to-back can see a newer eviction before an older
    /// surviving Wrote/refusal that was actually Note()'d first. EmitVerdict's own three-tier walk
    /// (Display.Flight.cs) never depends on cross-tier ordering (it does three full separate
    /// passes keyed on outcome/Evicted, not on list position), so this is safe for every reader
    /// this codebase has today -- but a future reader that assumes Entries is a straight timeline
    /// would be wrong to.</summary>
    public void Note(int id, long slotAddr, PaintOutcome outcome, int observed, bool evicted = false)
    {
        if (outcome == PaintOutcome.Wrote || outcome == PaintOutcome.AlreadyEqual)
            _settled.Add((id, slotAddr));

        if (outcome == PaintOutcome.AlreadyEqual) return;   // settled-lane only, see class doc

        var entry = new Entry(id, slotAddr, outcome, observed, evicted);
        if (_notable.Count < MaxEntries) { _notable.Add(entry); return; }

        if (!evicted) return;   // lane full, nothing lower-priority to make room for a routine entry
        for (int i = 0; i < _notable.Count; i++)
        {
            if (_notable[i].Evicted) continue;
            _notable[i] = entry;
            return;
        }
        // every existing entry is already an eviction: drop, same as any other over-cap entry.
    }

    /// <summary>True if <paramref name="id"/> settled (Wrote or AlreadyEqual) at ANY site noted
    /// this pass whose address satisfies <paramref name="addrPredicate"/>. Shaped for the LW-257
    /// spec's commit 2 pending-set clear predicate; not called by anything in commit 1.</summary>
    public bool Settled(int id, Func<long, bool> addrPredicate)
    {
        foreach (var (sid, addr) in _settled)
            if (sid == id && addrPredicate(addr)) return true;
        return false;
    }

    /// <summary>Reset both lanes for the next pass. Callers own the pass boundary (CardSites
    /// never clears this itself, since a caller may want to inspect a pass's results first).</summary>
    public void Clear() { _notable.Clear(); _settled.Clear(); }
}
