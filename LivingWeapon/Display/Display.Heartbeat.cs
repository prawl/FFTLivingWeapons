using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-257 commit 2: maintenance-beat plumbing shared by <see cref="Display.Tick"/> and
/// <see cref="Display.PaintCountsIfChanged"/>, so the on-field path (Engine.cs calls only
/// PaintCountsIfChanged there) repaints once a second too. Split from Display.cs (over the 200-line trigger).
/// Every beat runs one PaintAll regardless of what is pending; the pending set only watches whether each
/// changed id settled at a trusted site. After Tuning.CardPendingMaxBeats beats it logs one Debug line and
/// stops watching. An id that never settles would falsify [card-materializes-from-named-pool] (docs/LIVE_LEDGER.md).
/// </summary>
internal sealed partial class Display
{
    /// <summary>Weapon ids whose kill count changed but have not yet settled (CardVerdict.Settled).
    /// Value is the number of beats watched without settling. A watchlist, no paint is gated on
    /// it. Internal so tests can inspect it.</summary>
    internal readonly Dictionary<int, int> _pendingIds = new();

    /// <summary>Maintenance beat gate: true at most once per MaintenanceMs. It advances _lastMaintenanceMs
    /// whenever it fires, even if the caller skips its paint, so Tick and PaintCountsIfChanged share one
    /// clock.</summary>
    private bool MaintenanceDue(long now)
    {
        if (now - _lastMaintenanceMs < MaintenanceMs) return false;
        _lastMaintenanceMs = now;
        return true;
    }

    /// <summary>Compare current kill counts to the last snapshot. Each changed id is appended to
    /// <paramref name="changedIds"/> (StoryLines.RecomposeChanged input) and restaged in
    /// <see cref="_pendingIds"/> at beat 0. Returns true if any count changed.</summary>
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

    /// <summary>The maintenance beat's paint pass. With nothing pending it is the plain PaintAllTapped.
    /// Otherwise it runs one PaintAll with a local verdict rather than <see cref="_verdict"/> because
    /// DisplayFlightTests.No_recorder_means_the_verdict_ledger_is_never_allocated keeps that field null
    /// without a recorder. The pending branch therefore does not drain _verdict, so discovery entries
    /// queued by OnChunk wait for the next PaintAllTapped and carry about another MaintenanceMs of skew.</summary>
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

        // LW-257 commit 2 (spec 2.2): pool-paint only; no-op until coverage has latched once.
        if (_poolPaint && _poolCovered) ReOfferDrainedRegions();
    }

    /// <summary>Judge each id in <see cref="_pendingIds"/> against this beat's verdict. An id clears
    /// when it settled at a site inside a located, non-stale pool region. With an empty or stale region
    /// list (<see cref="PoolLocator.CachedRegions"/>), any settled site clears it, since the fresh paint
    /// may sit outside the old regions (Invalidate() no longer clears the cache). The sweep path
    /// (poolPaint false) takes the same branch.
    /// DisplayHeartbeatTests.ProcessPending_after_invalidate_still_settles_ids_painted_at_new_addresses pins it.
    ///
    /// Six full pool scans on the 2026-08-18 pass took 631 to 689 ticks (65.4 to 101.7s, old blocking
    /// LocateAll: 7 to 10s), so the permissive branch is common on a real tape. SnapshotRefreshMs
    /// (PoolScan.cs) should cut that by 29 to 32 percent; the next tape's "LW37 locate-complete" line
    /// gives the new figure.
    ///
    /// Clear/bump/drop are collected and applied after the loop for readability. On net8 overwriting an
    /// existing key or Remove mid-foreach is fine; only inserting a new key throws.</summary>
    private void ProcessPending(CardVerdict verdict)
    {
        if (_pendingIds.Count == 0) return;
        var regions = _poolLocator.CachedRegions;
        bool permissive = regions.Count == 0 || _poolLocator.RegionsStale;

        List<int>? toClear = null;
        List<(int id, int beats)>? toBump = null;
        List<int>? toDrop = null;

        foreach (var kv in _pendingIds)
        {
            int id = kv.Key;
            bool settled = permissive
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
