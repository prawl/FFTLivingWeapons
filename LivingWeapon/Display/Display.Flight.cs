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
    /// log. Internal (not private) so a test can cite the cap directly (LW-269), mirroring
    /// CoverageRecordBudget and LocateRecordBudget's own test-accessor convention.</summary>
    internal const int FlightRecordBudget = 64;
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

    /// <summary>LW-259: EmitVerdict's tier 1 (site-evicted) gets its OWN small reserve, never
    /// drawn from <see cref="_flightBudget"/> -- the same rationale as <see
    /// cref="CoverageRecordBudget"/> above (that field's own doc), applied to the traffic shape
    /// this arc found actually starving in the wild: the three-tier priority inside EmitVerdict
    /// only orders spending WITHIN one pass, but <see cref="_flightBudget"/> is shared across every
    /// pass in a window (only Invalidate() resets it), and a long battle's routine tier-2 paint
    /// lines can exhaust all 64 across MANY passes -- roughly eleven kills is enough, each kill
    /// emitting several paint lines -- well before a late tier-1 eviction (a strike-cap or a
    /// genuine buffer-reuse mismatch, the rare, expensive-to-lose signal) ever gets a pass to run
    /// in. 16 is generous headroom, not a measured ceiling: two full strike-cap storms (a single
    /// PaintAll pass tops out at CardVerdict.MaxEntries=256 entries, but tier-1 evictions within
    /// one pass are bounded by how many DISTINCT sites can genuinely strike-out or mismatch at
    /// once, nowhere near that) fit comfortably inside 16 on any tape observed so far.</summary>
    internal const int EvictedRecordBudget = 16;

    /// <summary>Internal (not private) so a test can drive this straight to the cap -- mirrors
    /// _coverageBudget's own test-accessor convention (this file).</summary>
    internal int _evictedBudget;

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
    /// not list order -- three separate passes over the same (&lt;=256-entry) list:
    ///
    /// 1. site-evicted, for every <see cref="CardVerdict.Entry.Evicted"/> entry. LW-259: spends
    ///    its OWN <see cref="_evictedBudget"/>/<see cref="EvictedRecordBudget"/> reserve, NOT <see
    ///    cref="_flightBudget"/> -- see that field's own doc for why. (Before LW-259 this tier
    ///    spent the shared budget too; CardVerdict.Note's own admission priority inside one pass
    ///    already kept evictions IN the 256-entry notable lane when full, but that only orders
    ///    entries WITHIN a pass -- it did nothing to stop many EARLIER passes' routine tier-2
    ///    paint lines from exhausting the shared budget before this tier ever ran.) Strikes short
    ///    of the cap correctly emit nothing here (Evicted is false), so the strike-limit eviction
    ///    on the Nth strike -- previously silent, since an earlier version of this method
    ///    special-cased AnchorMismatch only -- now has its own flight trace without also logging
    ///    every retry.
    /// 2. paint, for Wrote entries on KILLS sites only, spending <see cref="_flightBudget"/>:
    ///    Observed is -1 for a suffix site (CardSites.cs's ObservedFor -- a suffix slot holds a
    ///    tier word, never a count), so a negative Observed both marks "not a kills site" and
    ///    "nothing worth printing" in one check. `to=` only -- CardVerdict.Entry has no `from=`
    ///    (the LW-257 spec asked for one; the shape this commit shipped cannot carry it) -- so the
    ///    tape can say a slot now reads a value but cannot distinguish "painted 23 over 22" from
    ///    "painted 23 over garbage"; a real diff would need a second field this commit does not add.
    /// 3. site-refused, for SlotUnreadable/SlotShapeRefused/NotWritable, also spending <see
    ///    cref="_flightBudget"/> -- lowest priority: the site stays cached and simply retries next
    ///    pass, so losing this line to budget pressure costs the least. (A non-evicted
    ///    AnchorUnreadable strike is deliberately NOT included here either -- same "still cached,
    ///    will retry" reasoning as tier 1's own doc, and adding it would silently change the
    ///    tier-1 strike-quiet behavior this method already documents.)
    /// </summary>
    private void EmitVerdict(CardVerdict verdict)
    {
        foreach (var e in verdict.Entries)
        {
            // break, not return: this reserve is PRIVATE to tier 1 (EvictedRecordBudget's own
            // doc). A return here would abort EmitVerdict entirely once the reserve caps,
            // silencing tiers 2/3's SEPARATE _flightBudget lane for the rest of every later pass
            // in the window even while that budget sits untouched -- the starvation bug this
            // fix closes. Tiers 2/3's own `return`s below stay `return`: both spend the SAME
            // _flightBudget, so if one caps the other would refuse too -- mutually benign.
            if (_evictedBudget >= EvictedRecordBudget) break;
            if (!e.Evicted) continue;
            _recorder!("card", $"site-evicted id={e.Id} addr=0x{e.SlotAddr:X} reason={e.Outcome.Phrase()}");
            _evictedBudget++;
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
    /// doc for why the two must not share). LW-262: gained a `suffix=` field (CardSites.SuffixCount)
    /// alongside the pre-existing `kills=` one, so a tape can tell the two partitioned caps'
    /// occupancy apart directly instead of only inferring it from `sites=` minus `kills=`.
    /// <paramref name="snapshot"/> (LW-259 ride-along): the caller's OWN CardSites.Snapshot()
    /// copy, threaded through instead of this method taking a second one -- AnnounceCoverage
    /// already needs a full snapshot to count killsSites, so a private copy here was a pure
    /// duplicate whenever a recorder was actually wired. No behavior change: the snapshot is
    /// still only ever read below the same early-return this method always had.</summary>
    private void RecordCoverageIfTapped(IReadOnlyList<(long baseAddr, long size)> regions, int killsSites, string trigger, List<CardSites.Site> snapshot)
    {
        if (_recorder == null || _coverageBudget >= CoverageRecordBudget) return;
        var parts = new List<string>(regions.Count);
        foreach (var (rbase, rsize) in regions)
        {
            int count = 0;
            foreach (var s in snapshot) if (s.SlotAddr >= rbase && s.SlotAddr < rbase + rsize) count++;
            parts.Add($"base=0x{rbase:X}:{count}");
        }
        _recorder("card", $"coverage regions={regions.Count} sites={_sites.Count} kills={killsSites} suffix={_sites.SuffixCount} trigger={trigger} " + string.Join(" ", parts));
        _coverageBudget++;
    }

    /// <summary>LW-262: CardSites' cap-relief prune tap (CardSites.Admission.cs's _onPruneEvict
    /// ctor param, wired in Display.cs's constructor). Reuses the EXISTING <see
    /// cref="_flightBudget"/>/<see cref="FlightRecordBudget"/> tiers rather than a dedicated
    /// reserve -- deliberately UNCHANGED by LW-259 (this method still spends the shared budget
    /// exactly as before; only EmitVerdict's own tier 1 moved off it, see <see
    /// cref="EvictedRecordBudget"/>'s doc). LW-259 CORRECTION: every sentence below used to compare
    /// this tap's traffic shape against "EmitVerdict's tier 1", back when tier 1 drew from this
    /// SAME <see cref="_flightBudget"/> too -- that is no longer true, tier 1 now has its own
    /// reserve, so this prune tap's actual shared-budget neighbors today are EmitVerdict's tier 2
    /// (paint) and tier 3 (site-refused) lines, not tier 1. The comparison and the burst-risk
    /// reasoning below are otherwise unchanged from before that split -- read "EmitVerdict's
    /// eviction traffic" as "what tier 1 used to spend against this same budget" and the numbers
    /// still hold. CORRECTION (verifier F3): an earlier version of this doc claimed cap-relief
    /// pruning is not "a genuinely different traffic shape" from that eviction path -- that was
    /// backwards. It IS different: EmitVerdict's per-pass eviction traffic topped out at
    /// CardVerdict.MaxEntries (256) entries spread across one PaintAll call, while ONE cap-relief
    /// prune can evict 1000+ dead KILLS sites in a single burst (kills now use the full 2048
    /// ceiling with no suffix competition), which would exhaust this 64-record budget instantly
    /// and starve every tier-2/tier-3 paint or site-refused line for the rest of the window (tier
    /// 1's own site-evicted lines are safe from this burst since LW-259 -- they no longer share
    /// this budget at all). The budget choice stands anyway, on an honestly different
    /// basis: the burst risk is ACCEPTED, not overlooked, because a KILLS cap-relief prune should
    /// be rare post-partition in the first place -- the 2026-08-18 tape's own peak (726-728 of
    /// 2048) leaves wide headroom before the kills cap is ever hit at all, and the ONE path that
    /// WAS routinely hitting a cap (suffix) never prunes anymore (CardSites.Admission.cs's own
    /// "prune amplification bound"). A dedicated reserve was considered and rejected: sizing one
    /// for a worst-case 1000+ site burst means either a reserve nearly as large as
    /// FlightRecordBudget itself (defeating the point of a small separate tier) or one still too
    /// small to capture a real burst anyway -- so if a kills cap-relief burst DOES turn out to be
    /// common on a live tape, contrary to this rationale, the fix is to size a real reserve from
    /// that measurement, not to keep pretending the traffic shapes match. Format mirrors
    /// EmitVerdict's site-evicted line exactly, with a literal `reason=pruned-dead` in place of a
    /// PaintOutcome phrase (a cap-relief prune has no PaintOutcome to report -- it only ever asks
    /// "is this anchor Live", never attempts a paint).</summary>
    private void OnSitePruned(CardSites.Site s)
    {
        if (_recorder == null || _flightBudget >= FlightRecordBudget) return;
        _recorder("card", $"site-evicted id={s.Id} addr=0x{s.SlotAddr:X} reason=pruned-dead");
        _flightBudget++;
    }

    /// <summary>LW-261: PoolLocator.Step's completion tap, Display.PoolLocate.cs's own call site.
    /// Mirrors RecordCoverageIfTapped above: its own small reserve, never drawn from <see
    /// cref="_flightBudget"/> (see that field's own doc for why the two traffic shapes must not
    /// share) -- a scan completes at most a handful of times per Invalidate() window, nowhere near
    /// a per-site traffic shape, but it deserves its own budget rather than being able to starve
    /// or be starved by either existing lane.</summary>
    internal const int LocateRecordBudget = 8;
    internal int _locateFlightBudget;

    private void RecordLocateCompleteIfTapped(PoolLocator.LocateCompletion c)
    {
        if (_recorder == null || _locateFlightBudget >= LocateRecordBudget) return;
        _recorder("card", $"locate-complete regions={c.Regions} ticks={c.Ticks} bytes={c.Bytes} ms={c.ElapsedMs} trigger={c.Trigger}");
        _locateFlightBudget++;
    }
}
