using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-257 commit 2: the shared maintenance-beat plumbing <see cref="Display.Tick"/> and <see
/// cref="Display.PaintCountsIfChanged"/> both drive through, so the on-field path (Engine.cs's
/// ShouldPaintCard-false branch calls PaintCountsIfChanged, never Tick) gets the same once-a-
/// second repaint discipline the off-field/paused path already had inside Tick. Split out (the
/// 200-line seam CardSites.Verify.cs and Display.Flight.cs already drew in commit 1): Display.cs
/// was already over the 200-line trigger before this arc.
///
/// The pending set is a SETTLEMENT WATCHDOG, not a retry mechanism -- correction, round 2 review:
/// an earlier version of this doc (and Tuning.CardPendingMaxBeats's own) described it as buying
/// "extra passes" or a "retry budget". There are none. Both branches of RunMaintenance run the
/// exact same single PaintAll every beat regardless of what is pending (RunMaintenance's own doc)
/// -- a pending id gets no extra paint attempt it would not have gotten anyway. What the set
/// actually buys: PaintAll can only fix a site that is ALREADY cached, so the bug this arc closes
/// (a kill landing while no site for that id is cached yet -- Display.cs's old PaintCountsIfChanged
/// consumed the count-change edge into _lastCounts regardless, so once a site DID appear there was
/// no further edge left to notice it needed a value) is actually fixed by the hoisted BEAT alone
/// (every cached site gets repainted once a second, no matter which id changed it). The set's own
/// job is narrower: watch whether each changed id ever settles somewhere this arc trusts (Process
/// Pending's own doc), so a beat can tell "caught up" apart from "still dark" and, past a bounded
/// number of beats of never settling, stop watching and say so on the log (one Debug line) instead
/// of watching silently forever. That watch is also the signature that would falsify the
/// [card-materializes-from-named-pool] ledger row (docs/LIVE_LEDGER.md) if the pool-region premise
/// this arc leans on were ever wrong: an id that never settles despite a real paint existing
/// somewhere is exactly what that failure would look like.
/// </summary>
internal sealed partial class Display
{
    /// <summary>Weapon ids whose kill count changed but have not yet been CONFIRMED settled
    /// (CardVerdict.Settled) at a site this arc trusts (see ProcessPending's own doc for the
    /// exact predicate) -- a watchlist, not a work queue (this file's own class doc). Value is the
    /// count of maintenance beats this id has been WATCHED without settling; no paint attempt is
    /// gated on it. Internal so a test can inspect membership directly, mirroring _sites/_sweep/
    /// _verdict's own test-accessor convention (Display.cs, Display.Flight.cs).</summary>
    internal readonly Dictionary<int, int> _pendingIds = new();

    /// <summary>The shared maintenance-beat gate: true at most once every MaintenanceMs, and
    /// ALWAYS advances _lastMaintenanceMs when it fires -- regardless of what the caller goes on
    /// to do with the true. This mirrors the pre-commit-2 inline check it replaces byte for byte
    /// (Display.cs's old Tick body: `_lastMaintenanceMs = now;` ran whenever due, even on the
    /// tick where the count/target-change branch had already painted and the maintenance paint
    /// itself was then skipped) -- one shared clock consulted from both Tick and
    /// PaintCountsIfChanged is the whole point; two independently-updated timers would let the
    /// beat fire twice as often as intended whenever both call sites run close together.</summary>
    private bool MaintenanceDue(long now)
    {
        if (now - _lastMaintenanceMs < MaintenanceMs) return false;
        _lastMaintenanceMs = now;
        return true;
    }

    /// <summary>Compare current kill counts against the last snapshot for all tracked ids.
    /// Updates the snapshot on any change, appending each changed id to <paramref
    /// name="changedIds"/> (Reliquary Phase 1's StoryLines.RecomposeChanged input) and (re)staging
    /// it into <see cref="_pendingIds"/> at beat 0 -- LW-257 commit 2: a fresh change always gets a
    /// full fresh watch window, even if the same id was already pending from an earlier change
    /// that had not yet settled (this is bookkeeping for the watchdog, not a retry grant -- see
    /// this file's class doc). Returns true if any count changed. Moved here from Display.cs
    /// (unchanged otherwise) since its body now directly serves the pending-set feature this file
    /// owns.</summary>
    private bool CheckAndSnapshotCounts(List<int> changedIds)
    {
        bool changed = false;
        foreach (int id in _meta.Keys)
        {
            int cur = KillsFor(id);
            _lastCounts.TryGetValue(id, out int last);
            if (cur != last)
            {
                _lastCounts[id] = cur;
                changed = true;
                changedIds.Add(id);
                _pendingIds[id] = 0;
            }
        }
        return changed;
    }

    /// <summary>The maintenance beat's own paint pass. When nothing is pending this is exactly
    /// PaintAllTapped's ordinary repaint-every-cached-site pass -- byte-identical cost and
    /// behavior to pre-commit-2, and every existing DisplayMaintenanceTests/DisplayFlightTests
    /// case that drives Tick's maintenance branch still goes through this exact call. Only when
    /// <see cref="_pendingIds"/> is non-empty does this thread a CardVerdict through the SAME
    /// single PaintAll pass (never a second one -- the spec's own cost accounting is one
    /// maintenance PaintAll per beat) so ProcessPending has outcome data to judge each pending id
    /// against.
    ///
    /// The verdict used here is a LOCAL instance, deliberately never the <see cref="_verdict"/>
    /// field: DisplayFlightTests.No_recorder_means_the_verdict_ledger_is_never_allocated pins
    /// _verdict staying null whenever no recorder is wired, and pending-set tracking must work
    /// identically whether or not a recorder is wired (production always wires one via Flight.
    /// Record; most tests do not) -- so this cannot reuse that field. EmitVerdict (Display.
    /// Flight.cs) already takes the verdict as a parameter rather than reading _verdict itself, so
    /// this call itself is tapped correctly. NOT byte-identical to the no-pending case though
    /// (correction, round 2 review): _verdict (the field) is what OnChunk's discovery path feeds
    /// on this SAME Tick, and only PaintAllTapped ever drains it (EmitVerdict then Clear). A
    /// pending-branch beat takes the else arm here instead, so _verdict sits untouched -- any
    /// discovery entries already queued in it are neither emitted nor cleared this beat. They
    /// carry over to whatever PaintAllTapped call happens to run next (a later beat, or a
    /// count-changed edge), arriving behind THAT call's own entries and stamped with THAT later
    /// moment's clock time -- roughly another MaintenanceMs of skew on top of the skew OnChunk's
    /// own "let it ride" comment (Display.cs) already documents, not zero.</summary>
    private void RunMaintenance()
    {
        if (_pendingIds.Count == 0)
        {
            PaintAllTapped();
        }
        else
        {
            var verdict = new CardVerdict();
            _sites.PaintAll(KillsFor, verdict);
            if (_recorder != null) EmitVerdict(verdict);
            ProcessPending(verdict);
        }

        // LW-257 commit 2 (spec 2.2): pool-paint-only, and a cheap no-op until coverage has
        // actually latched at least once (Display.PoolPaint.cs's _regionCountAtCoverage is empty
        // until then, so ReOfferDrainedRegions returns immediately).
        if (_poolPaint && _poolCovered) ReOfferDrainedRegions();
    }

    /// <summary>Judge every id in <see cref="_pendingIds"/> against THIS beat's verdict. Settled
    /// (Wrote or AlreadyEqual, CardVerdict.Settled) at a site inside a currently-located pool
    /// region clears it. With NO located regions at all, any settled site clears it instead: the
    /// sweep never narrows down "which physical copy the card reads from" the way the pool path's
    /// named-pool premise does (PoolLocator.cs's own PREMISE STATUS doc), so demanding a
    /// located-region match there would just drive every id to the drop cap on every single kill.
    /// This permissive branch is not only "every poolPaint:false case (the sweep path)" (an
    /// earlier version of this doc said so, incompletely -- round 2 review): PoolLocator._cached
    /// stays empty until a Tick actually runs MaybePoolPaint, and the on-field path
    /// (PaintCountsIfChanged) never does, so it ALSO covers every on-field stretch after any
    /// Invalidate() even when poolPaint is true, until the next off-field Tick relocates. An id
    /// that neither clears nor hits the cap this beat has its watched-beat count bumped by exactly
    /// one (this file's class doc: a watch count, not a retry count -- no extra paint follows
    /// either way).
    ///
    /// Collects into snapshot lists rather than mutating _pendingIds while enumerating it. NOT
    /// because the runtime would throw either way (correction, round 2 review, independently
    /// verified on this box, net8.0): overwriting an EXISTING key's value, and Remove, both run
    /// clean mid-foreach on modern .NET -- only inserting a genuinely NEW key throws
    /// InvalidOperationException. The snapshot-then-apply shape is used here because it is clearer
    /// to read as three independent one-purpose passes (clear / bump / drop) than one loop
    /// mutating three different ways, not because the alternative is unsafe.</summary>
    private void ProcessPending(CardVerdict verdict)
    {
        if (_pendingIds.Count == 0) return;
        var regions = _poolLocator.CachedRegions;

        List<int>? toClear = null;
        List<(int id, int beats)>? toBump = null;
        List<int>? toDrop = null;

        foreach (var kv in _pendingIds)
        {
            int id = kv.Key;
            bool settled = regions.Count == 0
                ? verdict.Settled(id, _ => true)
                : verdict.Settled(id, addr => InAnyRegion(addr, regions));

            if (settled) { (toClear ??= new List<int>()).Add(id); continue; }

            int next = kv.Value + 1;
            if (next >= Tuning.CardPendingMaxBeats) (toDrop ??= new List<int>()).Add(id);
            else (toBump ??= new List<(int, int)>()).Add((id, next));
        }

        if (toClear != null) foreach (int id in toClear) _pendingIds.Remove(id);
        if (toBump != null) foreach (var (id, next) in toBump) _pendingIds[id] = next;
        if (toDrop != null)
            foreach (int id in toDrop)
            {
                _pendingIds.Remove(id);
                ModLogger.Debug(LogVerb.Display,
                    $"card pending id={id} gave up after {Tuning.CardPendingMaxBeats} maintenance beats without settling");
            }
    }

    private static bool InAnyRegion(long addr, IReadOnlyList<(long baseAddr, long size)> regions)
    {
        foreach (var (rbase, rsize) in regions)
            if (addr >= rbase && addr < rbase + rsize) return true;
        return false;
    }
}
