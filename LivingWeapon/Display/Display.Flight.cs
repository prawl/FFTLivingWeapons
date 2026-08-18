using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-257 commit 1: the equip-card painter's flight-recorder taps and the CardVerdict plumbing
/// that feeds them. Split out of Display.cs (the same seam CardSites.Verify.cs draws in this same
/// commit) so the diagnostic/observability path stays out of the paint-orchestration file it
/// instruments. Every record emitted here reuses the frozen "card" flight type -- no new type
/// string, docs/LOGGING.md's vocabulary freeze -- and everything in this file is a no-op unless a
/// recorder was wired at construction (Engine.cs passes Flight.Record; every existing test passes
/// none): <see cref="_verdict"/> itself stays null, CardSites.PaintAll's verdict-taking overload
/// treats null as a pure no-op ledger skip, and neither EmitVerdict nor RecordCoverageIfTapped
/// ever runs.
/// </summary>
internal sealed partial class Display
{
    private readonly Action<string, string>? _recorder;

    /// <summary>Non-null only when <see cref="_recorder"/> is wired -- see the class doc. Kept
    /// conditional (not always-allocated) so the common no-recorder case (every existing test)
    /// costs nothing beyond the null check already needed for the tap itself. Internal so a test
    /// can pin "no recorder means the allocation path is never entered" directly, mirroring
    /// _sites/_stories's own test-accessor convention (Display.cs).</summary>
    internal readonly CardVerdict? _verdict;

    /// <summary>Records emitted per window before EmitVerdict starts dropping them silently. A
    /// window is one Invalidate() cycle (battle edge / status-card reopen / new game) -- the same
    /// boundary CardSites' own cache already resets on. Diagnostic headroom, not a hard
    /// requirement: dropping the tail of a storm is fine, this is a flight recorder, not an audit
    /// log.</summary>
    private const int FlightRecordBudget = 64;
    private int _flightBudget;

    /// <summary>Round-3 review (F3): coverage records get their OWN small reserve, never drawn
    /// from <see cref="_flightBudget"/>. That budget is shared by two very different traffic
    /// shapes -- up to 64 site-evicted/paint lines from a single pass, versus at most one or two
    /// coverage-latch lines a window -- so a genuine eviction storm could fill the shared budget
    /// and silently drop the ONE coverage line that explains it, which is exactly the
    /// eviction-plus-relatch correlation a live reader needs. 8 is generous: RecordCoverageIfTapped
    /// fires once per false-&gt;true (or count re-latch) transition, never per site.</summary>
    internal const int CoverageRecordBudget = 8;

    /// <summary>Internal (not private) so a test can drive this straight to the cap without
    /// manufacturing CoverageRecordBudget real coverage-latch transitions -- mirrors _verdict's
    /// own test-accessor convention (this file).</summary>
    internal int _coverageBudget;

    /// <summary>PaintAll wrapped with the verdict ledger and its flight taps. Every one of
    /// Display's three PaintAll call sites goes through this instead of calling CardSites.PaintAll
    /// directly, so no call site can forget it: the combined count/target-change branch in Tick
    /// (ONE call site gated by countsChanged-OR-targetsChanged, not two separate edges), the
    /// maintenance cadence (also in Tick), and PaintCountsIfChanged's own narrow edge.</summary>
    private void PaintAllTapped()
    {
        _sites.PaintAll(KillsFor, _verdict);
        if (_verdict == null) return;   // no recorder wired: pure no-op ledger skip (see class doc)
        EmitVerdict(_verdict);
        _verdict.Clear();
    }

    /// <summary>Walks one pass's notable-lane entries (CardVerdict's class doc) in PRIORITY order,
    /// not list order -- three separate passes over the same (&lt;=256-entry) list, spending
    /// <see cref="_flightBudget"/> on the highest tier first before moving to the next:
    ///
    /// 1. site-evicted, for every <see cref="CardVerdict.Entry.Evicted"/> entry. The rare,
    ///    expensive-to-lose signal -- a strike-cap or a genuine buffer-reuse mismatch -- gets first
    ///    claim on the budget so a routine paint storm can never crowd it out (round-4 review, item
    ///    1: CardVerdict.Note's own admission priority already keeps evictions IN the lane when
    ///    full; this is the matching priority on the way OUT). Strikes short of the cap correctly
    ///    emit nothing here (Evicted is false), so the strike-limit eviction on the Nth strike --
    ///    previously silent, since an earlier version of this method special-cased AnchorMismatch
    ///    only -- now has its own flight trace without also logging every retry.
    /// 2. paint, for Wrote entries on KILLS sites only: Observed is -1 for a suffix site
    ///    (CardSites.cs's ObservedFor -- a suffix slot holds a tier word, never a count), so a
    ///    negative Observed both marks "not a kills site" and "nothing worth printing" in one
    ///    check. `to=` only -- CardVerdict.Entry has no `from=` (the LW-257 spec asked for one; the
    ///    shape this commit shipped cannot carry it) -- so the tape can say a slot now reads a
    ///    value but cannot distinguish "painted 23 over 22" from "painted 23 over garbage"; a real
    ///    diff would need a second field this commit does not add.
    /// 3. site-refused, for SlotUnreadable/SlotShapeRefused/NotWritable -- lowest priority: the
    ///    site stays cached and simply retries next pass, so losing this line to budget pressure
    ///    costs the least. (A non-evicted AnchorUnreadable strike is deliberately NOT included
    ///    here either -- same "still cached, will retry" reasoning as tier 1's own doc, and adding
    ///    it would silently change the tier-1 strike-quiet behavior this method already documents.)
    /// </summary>
    private void EmitVerdict(CardVerdict verdict)
    {
        foreach (var e in verdict.Entries)
        {
            if (_flightBudget >= FlightRecordBudget) return;
            if (!e.Evicted) continue;
            _recorder!("card", $"site-evicted id={e.Id} addr=0x{e.SlotAddr:X} reason={e.Outcome.Phrase()}");
            _flightBudget++;
        }
        foreach (var e in verdict.Entries)
        {
            if (_flightBudget >= FlightRecordBudget) return;
            if (e.Evicted || e.Outcome != PaintOutcome.Wrote || e.Observed < 0) continue;
            _recorder!("card", $"paint id={e.Id} to={e.Observed} addr=0x{e.SlotAddr:X}");
            _flightBudget++;
        }
        foreach (var e in verdict.Entries)
        {
            if (_flightBudget >= FlightRecordBudget) return;
            if (e.Outcome != PaintOutcome.SlotUnreadable && e.Outcome != PaintOutcome.SlotShapeRefused
                && e.Outcome != PaintOutcome.NotWritable) continue;
            _recorder!("card", $"site-refused id={e.Id} addr=0x{e.SlotAddr:X} reason={e.Outcome.Phrase()}");
            _flightBudget++;
        }
    }

    /// <summary>AnnounceCoverage's flight tap (Display.PoolPaint.cs). Separate from the two
    /// per-site records above -- one line per coverage LATCH, not per site -- AND spends its own
    /// <see cref="CoverageRecordBudget"/>, never <see cref="_flightBudget"/> (see that field's own
    /// doc for why the two must not share).</summary>
    private void RecordCoverageIfTapped(IReadOnlyList<(long baseAddr, long size)> regions, int killsSites, string trigger)
    {
        if (_recorder == null || _coverageBudget >= CoverageRecordBudget) return;
        var snapshot = _sites.Snapshot();
        var parts = new List<string>(regions.Count);
        foreach (var (rbase, rsize) in regions)
        {
            int count = 0;
            foreach (var s in snapshot) if (s.SlotAddr >= rbase && s.SlotAddr < rbase + rsize) count++;
            parts.Add($"base=0x{rbase:X}:{count}");
        }
        _recorder("card", $"coverage regions={regions.Count} sites={_sites.Count} kills={killsSites} trigger={trigger} " + string.Join(" ", parts));
        _coverageBudget++;
    }
}
