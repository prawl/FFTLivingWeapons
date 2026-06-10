using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Paints the equip card for every weapon in the loaded nxd: the 2-char name suffix
/// (+/+2/+3), the per-weapon "Kills NNNN" counter, and the equipped-weapon WP number.
///
/// Architecture (v2): one <see cref="CardPatterns"/> built at ctor; a byte-budgeted
/// <see cref="DisplaySweep"/> that walks committed heap memory across Ticks without
/// freezing the engine loop; a <see cref="CardSites"/> cache that re-verifies ownership
/// anchors before writing (prevents a freed/reused UI buffer from getting a stale count
/// stamped into it); and an onChunk callback that discovers and paints sites as the
/// sweep goes, so a newly-found card is painted within the same generation chunk
/// rather than waiting for the sweep to complete.
///
/// Key invariants:
/// - Attribution searches ALL weapon flavors (not just the equipped pair) so unequipped
///   and hovered cards also show correct counts.  The nearest flavor before a "Kills: "
///   hit is that weapon's own, tying the site to the right id.
/// - The target set drives suffix painting; the sweep covers kills painting for every id.
///   An empty target set never returns early: all cards still receive their true counts.
/// - The sweep is byte-budgeted so a single Tick costs at most budget + one chunk, never
///   locking the 33ms engine loop the way an unbounded full scan did.
/// - WpScratch is keyed by the mirror weapon (the card currently on screen), not roster
///   slot 0, which previously wrote Ramza's boost while viewing another unit.
/// - All reads and writes go through <see cref="IGameMemory"/> (RPM/WPM-backed in
///   production), so a freed UI buffer yields a safe miss, never a crash.
/// </summary>
internal sealed partial class Display
{
    private const long BudgetInBattle    = 8L  * 1024 * 1024;
    private const long BudgetOutOfBattle = 16L * 1024 * 1024;
    private const int  RotationSlice     = 8;

    /// <summary>Cadence for the maintenance PaintAll call that drains dead sites and
    /// repaints any stale on-screen copy without waiting for a kill-count change.</summary>
    internal const long MaintenanceMs = 1000;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int>        _kills;
    private readonly IGameMemory                 _mem;
    internal readonly CardPatterns               _pats;
    internal readonly DisplaySweep               _sweep;
    internal readonly CardSites                  _sites;
    private readonly Func<long>                  _nowMs;

    private readonly Dictionary<int, int> _lastCounts = new();
    private HashSet<int> _lastTargets = new();

    // Timestamp of the last maintenance PaintAll; initialised to -1 (before any real clock
    // value) so the first Tick always triggers the maintenance pass.  Using long.MinValue
    // would overflow on the subtraction `now - _lastMaintenanceMs` since now >= 0.
    private long _lastMaintenanceMs = -1;

    // Persistent rotation cursor advanced by the number of non-target ids taken per chunk
    // so successive chunks and passes cover all ids without waiting for a new generation.
    internal int _rotCursor = 0;

    // Generation number at the last log line so we log once per completion.
    private long _lastLoggedGen = -1;

    public Display(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory mem,
                   Func<long>? nowMs = null)
    {
        _meta   = meta;
        _kills  = kills;
        _mem    = mem;
        _nowMs  = nowMs ?? (() => Environment.TickCount64);
        _pats   = new CardPatterns(meta);
        _sweep  = new DisplaySweep(mem, _nowMs);
        _sites  = new CardSites(mem, _pats);
    }

    /// <summary>Drop the site cache and start a new sweep generation on the next Tick.
    /// Call on battle exit or any event that reallocates the menu's render buffers.</summary>
    public void Invalidate()
    {
        _sites.Clear();
        _sweep.Invalidate();
        _lastTargets = new HashSet<int>();
    }

    /// <summary>Drive one display cycle. <paramref name="inBattle"/> true shrinks the byte
    /// budget to avoid competing with the kill-poll path during a live fight.</summary>
    public void Tick(bool inBattle)
    {
        // Gather the weapons whose NAME we actively track for suffix painting:
        // both mirror slots, filtered to valid tracked ids.  No tier gate -- the old gate
        // suppressed counts for sub-threshold weapons entirely (live bug: "tier-0 never painted").
        var targets = BuildTargets();

        // Count-change check over ALL meta ids (not just targets) so non-equipped weapons
        // also trigger a rescan when their kill count changes.
        bool countsChanged = CheckAndSnapshotCounts();
        bool targetsChanged = !targets.SetEquals(_lastTargets);

        if (countsChanged || targetsChanged)
        {
            _sweep.RequestRescan();
            _sites.PaintAll(KillsFor);
        }

        // Maintenance repaint: PaintAll on a clock cadence to drain dead sites and
        // refresh any stale on-screen copy without waiting for a kill-count change.
        // skip-if-equal keeps steady-state writes at zero; this is cheap in the common case.
        long now = _nowMs();
        if (now - _lastMaintenanceMs >= MaintenanceMs)
        {
            _lastMaintenanceMs = now;
            if (!countsChanged && !targetsChanged)
                _sites.PaintAll(KillsFor);
        }

        // Target change means fresh card buffers may have appeared; start a new generation
        // after the min-gap floor rather than waiting up to GenerationRestMs (90s).
        if (targetsChanged)
            _sweep.Invalidate();

        _lastTargets = targets;

        long budget = inBattle ? BudgetInBattle : BudgetOutOfBattle;
        _sweep.Tick(budget, OnChunk);

        // Log once per generation completion so the log captures each full scan.
        if (_sweep.IsComplete && _sweep.Generation != _lastLoggedGen)
        {
            _lastLoggedGen = _sweep.Generation;
            Log.Info("display: memory sweep #" + _sweep.Generation + " finished -- maintaining " + _sites.Count + " card-text spots");
        }

        // WpScratch: keyed by the mirror weapon (the card on screen), NOT roster slot 0.
        // Roster-slot-0 keying painted Ramza's boost while viewing any other unit.
        PaintWpScratch();
    }

    // ─── private helpers ──────────────────────────────────────────────────────

    private HashSet<int> BuildTargets()
    {
        var t = new HashSet<int>();
        AddTarget(t, _mem.U16(Offsets.MirrorWeapon));
        AddTarget(t, _mem.U16(Offsets.MirrorOffHand));
        return t;
    }

    private void AddTarget(HashSet<int> targets, int id)
    {
        if (id > 0 && id < 0xFFFF && _meta.ContainsKey(id))
            targets.Add(id);
    }

    /// <summary>Compare current kill counts against the last snapshot for all tracked ids.
    /// Updates the snapshot on any change.  Returns true if any count changed.</summary>
    private bool CheckAndSnapshotCounts()
    {
        bool changed = false;
        foreach (int id in _meta.Keys)
        {
            int cur = KillsFor(id);
            _lastCounts.TryGetValue(id, out int last);
            if (cur != last) { _lastCounts[id] = cur; changed = true; }
        }
        return changed;
    }

    internal int KillsFor(int id) => _kills.TryGetValue(id, out int k) ? k : 0;
}
