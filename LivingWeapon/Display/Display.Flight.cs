using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-257: flight-recorder taps for the equip-card painter and the CardVerdict plumbing that
/// feeds them, split out of Display.cs (CardSites.Verify.cs is the matching split). Records
/// reuse the frozen "card" flight type (docs/LOGGING.md). Everything here is a no-op unless a
/// recorder was passed at construction (Engine.cs passes Flight.Record; tests pass none).
/// </summary>
internal sealed partial class Display
{
    private readonly Action<string, string>? _recorder;

    /// <summary>Non-null only when <see cref="_recorder"/> is wired. Internal so a test can check
    /// the no-recorder case never allocates it.</summary>
    internal readonly CardVerdict? _verdict;

    /// <summary>Records emitted per window before EmitVerdict drops the rest. A window is one
    /// Invalidate() cycle (battle edge, status-card reopen, new game). Dropping a storm tail is
    /// fine for a flight recorder. Internal so tests can cite the cap (LW-269).</summary>
    internal const int FlightRecordBudget = 64;
    private int _flightBudget;

    /// <summary>Coverage records get their own reserve so an eviction storm cannot drop the one
    /// coverage line that explains it. RecordCoverageIfTapped fires once per latch transition,
    /// so 8 is plenty.</summary>
    internal const int CoverageRecordBudget = 8;

    /// <summary>Internal so a test can drive it to the cap.</summary>
    internal int _coverageBudget;

    /// <summary>LW-259: site-evicted records (EmitVerdict tier 1) get their own reserve. The
    /// shared <see cref="_flightBudget"/> lasts a whole window, and routine tier-2 paint lines
    /// (about eleven kills worth) can use up all 64 before a rare late eviction is emitted.
    /// 16 is headroom, not a measured ceiling.</summary>
    internal const int EvictedRecordBudget = 16;

    /// <summary>Internal so a test can drive it to the cap.</summary>
    internal int _evictedBudget;

    /// <summary>PaintAll wrapped with the verdict ledger and its flight taps. All three Display
    /// PaintAll call sites (the Tick count/target branch, the maintenance cadence, and
    /// PaintCountsIfChanged) go through this.</summary>
    private void PaintAllTapped()
    {
        _sites.PaintAll(KillsFor, _verdict);
        if (_verdict == null) return;   // no recorder wired
        EmitVerdict(_verdict);
        _verdict.Clear();
    }

    /// <summary>Walks one pass's notable entries (at most 256) in priority order, as three passes:
    /// 1. site-evicted, spending <see cref="_evictedBudget"/> (LW-259). Strikes short of the cap
    ///    emit nothing; the Nth-strike eviction does.
    /// 2. paint, for Wrote entries on kills sites only (Observed is -1 for a suffix site, CardSites.cs
    ///    ObservedFor). Logs `to=` only; CardVerdict.Entry carries no `from=`, so the tape cannot
    ///    tell "23 over 22" from "23 over garbage".
    /// 3. site-refused (SlotUnreadable, SlotShapeRefused, NotWritable): lowest priority, since the
    ///    site stays cached and retries next pass. A non-evicted AnchorUnreadable strike is left out.
    /// Tiers 2 and 3 spend <see cref="_flightBudget"/>.
    /// </summary>
    private void EmitVerdict(CardVerdict verdict)
    {
        foreach (var e in verdict.Entries)
        {
            // break, not return: tier 1 has a private reserve, so hitting its cap must not
            // silence tiers 2/3. Their returns below are fine because they share one budget.
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

    /// <summary>AnnounceCoverage's flight tap (Display.PoolPaint.cs): one line per coverage latch,
    /// spending <see cref="CoverageRecordBudget"/>. LW-262 added `suffix=` (CardSites.SuffixCount)
    /// next to `kills=`. <paramref name="snapshot"/> is the caller's CardSites.Snapshot() copy,
    /// passed in to avoid a second one (LW-259).</summary>
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

    /// <summary>LW-262: tap for CardSites' cap-relief prune (_onPruneEvict in CardSites.Admission.cs,
    /// wired in Display.cs). Spends the shared <see cref="_flightBudget"/>; since LW-259 its
    /// neighbors are EmitVerdict tiers 2 and 3, not tier 1. One prune can evict 1000+ dead kills
    /// sites in a burst and exhaust the 64 records, starving tier 2/3 lines for the window. That is
    /// accepted: kills prunes should be rare (2026-08-18 tape peaked at 726-728 of 2048) and suffix
    /// sites no longer prune (CardSites.Admission.cs "prune amplification bound"). A dedicated
    /// reserve would have to be nearly as large as the budget or too small to catch a burst; if
    /// bursts show up on a real tape, size one from that. Same format as the site-evicted line,
    /// with `reason=pruned-dead` because a prune has no PaintOutcome.</summary>
    private void OnSitePruned(CardSites.Site s)
    {
        if (_recorder == null || _flightBudget >= FlightRecordBudget) return;
        _recorder("card", $"site-evicted id={s.Id} addr=0x{s.SlotAddr:X} reason=pruned-dead");
        _flightBudget++;
    }

    /// <summary>LW-261: tap for PoolLocator.Step completion (Display.PoolLocate.cs), with its own
    /// reserve. LW-264: on a cold boot before the pool exists, the empty-retry lane completes about
    /// once a second (LogLocateComplete, PoolLocator.Log.cs), so the cap keeps the tape readable.</summary>
    internal const int LocateRecordBudget = 8;
    internal int _locateFlightBudget;

    private void RecordLocateCompleteIfTapped(PoolLocator.LocateCompletion c)
    {
        if (_recorder == null || _locateFlightBudget >= LocateRecordBudget) return;
        _recorder("card", $"locate-complete regions={c.Regions} ticks={c.Ticks} bytes={c.Bytes} ms={c.ElapsedMs} trigger={c.Trigger}");
        _locateFlightBudget++;
    }
}
