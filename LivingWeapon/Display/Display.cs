using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Paints the equip card for every weapon in the loaded nxd. The name suffix (+/+2/+3),
/// the per-weapon "Kills NNNN" counter, and the equipped-weapon WP number.
/// A <see cref="CardPatterns"/> set drives a byte-budgeted <see cref="DisplaySweep"/> over heap memory,
/// and <see cref="CardSites"/> re-checks ownership anchors before writing so a freed UI buffer
/// never gets a stale count. Attribution searches all weapon flavors so unequipped and hovered cards
/// are right too. WpScratch is keyed by the mirror weapon (the card on screen), since keying by roster
/// slot 0 once wrote Ramza's boost while viewing another unit. Access goes through <see cref="IGameMemory"/>.
/// </summary>
internal sealed partial class Display
{
    private const long BudgetInBattle = 8L * 1024 * 1024;
    private const long BudgetOutOfBattle = 16L * 1024 * 1024;

    /// <summary>Cadence for the maintenance PaintAll call that drains dead sites and
    /// repaints any stale on-screen copy without waiting for a kill-count change.</summary>
    internal const long MaintenanceMs = 1000;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private readonly IGameMemory _mem;
    private readonly CardPatterns _pats;  // card-pattern set consumed by Display painting
    internal readonly DisplaySweep _sweep;  // DisplayPoolPaintTests reads Generation/IsComplete (pool-paint skip proof)
    internal readonly CardSites _sites;  // DisplayMaintenanceTests reads Count
    private readonly WpScratchPainter _wpScratch;
    private readonly Func<long> _nowMs;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md): card-story composer. Null when no LegendStore
    // is wired, which falls back to baked-only anchor verification. Internal for tests.
    internal readonly StoryLines? _stories;

    private readonly Dictionary<int, int> _lastCounts = new();
    private readonly SuffixRotation _rotation = new();
    private HashSet<int> _lastTargets = new();

    // Last maintenance PaintAll; -1 so the first Tick runs it (long.MinValue would overflow
    // the `now, _lastMaintenanceMs` subtraction).
    private long _lastMaintenanceMs = -1;

    // Generation number at the last log line so we log once per completion.
    private long _lastLoggedGen = -1;

    // Time of the first Tick, -1 until then. Engine only ticks Display after the
    // launch guard arms, so this is the armed moment; Display.PoolPaint.cs times kill-counter readiness from it.
    private long _firstTickMs = -1;

    /// <param name="legends">Reliquary Phase 1 deed ledger (docs/RELIQUARY_AC.md); null skips card-story composing.</param>
    /// <param name="poolPaint">Pool-paint gate; null means false (whole-heap sweep) so tests are independent of the
    /// release flag. Engine passes Tuning.PoolPaintEnabled.</param>
    /// <param name="recorder">Flight-recorder tap; null emits no flight lines (see Display.Flight.cs).
    /// Engine passes Flight.Record.</param>
    /// <param name="poolRegionSidecarPath">path of pool_regions.json, passed to PoolLocator. Null
    /// disables the warm-start seed and its write on completion.</param>
    public Display(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory mem,
                   Func<long>? nowMs = null, LegendStore? legends = null, bool? poolPaint = null,
                   Action<string, string>? recorder = null, string? poolRegionSidecarPath = null)
    {
        _meta = meta;
        _kills = kills;
        _mem = mem;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _pats = new CardPatterns(meta);
        _poolPaint = poolPaint ?? false;
        _poolLocator = new PoolLocator(mem, _pats, _nowMs, poolRegionSidecarPath);
        _sweep = new DisplaySweep(mem, _nowMs);
        // StoryLines owns EarnedAnchors, so build it before CardSites, which takes them. SeedAtStartup
        // recomposes each weapon's line from persisted deeds so the first paint is already current.
        _stories = legends != null ? new StoryLines(legends, meta, kills, _pats) : null;
        _stories?.SeedAtStartup();
        // Eviction tap for the cap-relief prune. Safe to wire before _recorder is
        // assigned below, since OnSitePruned only reads it when invoked later.
        _sites = new CardSites(mem, _pats, _stories?.Anchors, OnSitePruned);
        _wpScratch = new WpScratchPainter(mem, meta, KillsFor);

        // The sweep's lookback prefix must hold the longest anchor plus the widest slot, or a card
        // straddling a chunk boundary never verifies. Log and continue; a throw would take the game down.
        // Earned anchors stay within MaxAnchorLen because EarnedAnchors forces their encoded length
        // equal to the baked Flavor pattern (DisplayLookbackInvariantTests.FitsLookback_with_earned_patterns_registered).
        if (!_pats.FitsLookback(DisplaySweep.Lookback))
        {
            ModLogger.Error(LogVerb.Display, "The equip-card painter is misconfigured and may fail to paint some kill counters.");
            ModLogger.Debug(LogVerb.Trace, "painter misconfiguration detail (lookback="
                      + DisplaySweep.Lookback + " < maxAnchor=" + _pats.MaxAnchorLen + " + slot)");
        }

        // Same check for the trailing slack. The bidirectional attribution search (CardScanner) can
        // find a flavor after its "Kills: " hit, so the slack must fit anchor + literal + slot.
        if (!_pats.FitsTrailSlack(DisplaySweep.TrailSlack))
        {
            ModLogger.Error(LogVerb.Display, "The equip-card painter is misconfigured and may fail to paint kill counters near a chunk boundary.");
            ModLogger.Debug(LogVerb.Trace, "painter misconfiguration detail (trailSlack="
                      + DisplaySweep.TrailSlack + " < maxAnchor=" + _pats.MaxAnchorLen + " + slot)");
        }

        // Display.Flight.cs owns the tap. _verdict stays null without a recorder.
        _recorder = recorder;
        _verdict = recorder != null ? new CardVerdict() : null;
    }

    /// <summary>Drop the site cache and start a new sweep generation on the next Tick.
    /// Call on battle exit or any event that reallocates the menu's render buffers.</summary>
    public void Invalidate()
    {
        _sites.Clear();
        _sweep.Invalidate();
        _lastTargets = new HashSet<int>();
        _poolCovered = false;
        _poolLocator.Invalidate();
        _flightBudget = 0;     // a new coverage/cache window gets a fresh record budget
        _coverageBudget = 0;   // its own reserve, reset on the same window boundary
        _locateFlightBudget = 0;   // the locate-complete tap's own reserve, same window boundary
        _evictedBudget = 0;    // tier 1's own reserve, reset on the same window boundary
        _pendingIds.Clear();   // _sites was wiped, so pending watches would burn all CardPendingMaxBeats and log a false "gave up"
    }

    /// <summary>In-battle repaint driven by a kill-count change alone, called on ticks
    /// ShouldPaintCard skips so a mid-fight kill reaches the equip card. Recompose runs before PaintAll,
    /// as in <see cref="Tick"/>, so a story-line rotation is never one call stale. It latches
    /// RequestRescan but never steps the sweep or calls Invalidate (the pool relocate runs on
    /// Engine's own "pool-locate" lane every tick). When nothing painted, it also runs
    /// the shared maintenance beat (Display.Heartbeat.cs), which may re-offer one drained pool region
    /// (Display.PoolDrain.cs). That can clear _poolCovered; on the on-field path the flag waits for the
    /// next Tick(true) to act on it.</summary>
    public void PaintCountsIfChanged()
    {
        var changedIds = new List<int>();
        bool countsChanged = CheckAndSnapshotCounts(changedIds);

        if (countsChanged)
        {
            _stories?.RecomposeChanged(changedIds);
            _sweep.RequestRescan();
            if (_sites.Count > 0) PaintAllTapped();
        }

        // Maintenance beat shared with Tick (Display.Heartbeat.cs); skipped if a count change just painted.
        if (MaintenanceDue(_nowMs()) && !countsChanged)
            RunMaintenance();
    }

    /// <summary>Drive one display cycle. <paramref name="inBattle"/> true shrinks the byte budget
    /// so the sweep does not compete with kill polling mid-fight.</summary>
    public void Tick(bool inBattle)
    {
        if (_firstTickMs < 0) _firstTickMs = _nowMs();

        // Weapons whose name suffix we track. Both mirror slots, valid ids only. No tier gate
        // (it hid counts for sub-threshold weapons, "tier-0 never painted").
        var targets = BuildTargets();

        // Count check covers all meta ids so unequipped weapons also trigger a rescan.
        // changedIds feeds StoryLines recompose; a deed only changes with a tally increment (KillTracker.CreditKill).
        var changedIds = new List<int>();
        bool countsChanged = CheckAndSnapshotCounts(changedIds);
        bool targetsChanged = !targets.SetEquals(_lastTargets);

        // Recompose before painting so this Tick's repaint sees the fresh line.
        if (countsChanged) _stories?.RecomposeChanged(changedIds);

        if (countsChanged || targetsChanged)
        {
            _sweep.RequestRescan();
            PaintAllTapped();
        }

        // Maintenance repaint on a clock cadence (Display.Heartbeat.cs): drains dead sites, refreshes
        // stale copies, checks pending ids for settlement. Skip-if-equal keeps steady-state writes at zero.
        // MaintenanceDue advances the shared clock even when the paint below is skipped.
        if (MaintenanceDue(_nowMs()) && !countsChanged && !targetsChanged)
            RunMaintenance();

        // New targets may mean fresh card buffers; start a new generation after the min-gap floor.
        if (targetsChanged)
            _sweep.Invalidate();

        _lastTargets = targets;

        if (!(_poolPaint && MaybePoolPaint()))
        {
            long budget = inBattle ? BudgetInBattle : BudgetOutOfBattle;
            // allSuffixes stays false to keep the sweep's per-chunk cost unchanged; pinned by
            // DisplayPoolPaintTests.Sweep_path_still_limits_suffix_search_to_targets_plus_rotation.
            _sweep.Tick(budget, (buf, lookback, searchable, bufBaseAddr) => OnChunk(buf, lookback, searchable, bufBaseAddr, allSuffixes: false));
        }

        // Log once per generation. Generation 1 is the per-launch liveness canary (Info); later ones are Debug.
        if (_sweep.IsComplete && _sweep.Generation != _lastLoggedGen)
        {
            _lastLoggedGen = _sweep.Generation;
            if (_sweep.Generation == 1)
                ModLogger.Event(LogVerb.Display, $"The card display sweep completed its first pass, maintaining {_sites.Count} card-text spots.");
            else
                ModLogger.Debug(LogVerb.Display, "memory sweep number " + _sweep.Generation + " finished; maintaining " + _sites.Count + " card-text spots");
        }

        // WpScratch is keyed by the mirror weapon (the card on screen), not roster slot 0.
        _wpScratch.Paint();
    }

    // ─── private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Called by the sweep (or the pool path, Display.PoolPaint.cs) for each offered chunk. Registers
    /// kills and suffix sites and paints only the newly added ones, avoiding a verify storm on every hit chunk.
    /// Suffix search covers the targets plus a rotation slice of ids with kills hits (<see cref="SuffixRotation"/>).
    /// With <paramref name="allSuffixes"/> (pool path) every tracked id is searched, because pool
    /// suffix bytes can go stale independent of the tally reset and a small pool chunk is cheap.
    /// </summary>
    private void OnChunk(byte[] buf, int lookback, int searchable, long bufBaseAddr, bool allSuffixes)
    {
        var killsHits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, _pats, killsHits, _stories?.Anchors);

        // Ids with kills hits feed the rotation slice.
        var hitIds = new HashSet<int>();
        foreach (var h in killsHits) hitIds.Add(h.Id);

        // Suffix pass. Targets plus all ids (pool path) or the rotation slice (sweep path).
        var suffixIds = new HashSet<int>(_lastTargets);
        if (hitIds.Count > 0)
        {
            if (allSuffixes) suffixIds.UnionWith(_meta.Keys);
            else suffixIds.UnionWith(_rotation.Take(hitIds, _lastTargets));
        }

        var suffixHits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, lookback, searchable, _pats, suffixIds, suffixHits);

        // Collect new sites so only those get painted.
        var newSites = new List<CardSites.Site>();

        foreach (var h in killsHits)
        {
            long slotAddr = bufBaseAddr + h.SlotPos;
            long anchorAddr = h.FlavorPos >= 0 ? bufBaseAddr + h.FlavorPos : slotAddr;
            var site = new CardSites.Site(h.Id, h.Enc, slotAddr, anchorAddr, IsKills: true);
            if (_sites.Add(site)) newSites.Add(site);
        }

        foreach (var h in suffixHits)
        {
            long slotAddr = bufBaseAddr + h.SlotPos;
            long anchorAddr = bufBaseAddr + h.NamePos;
            var site = new CardSites.Site(h.Id, h.Enc, slotAddr, anchorAddr, IsKills: false);
            if (_sites.Add(site)) newSites.Add(site);
        }

        if (newSites.Count > 0)
        {
            // Paint only the new sites. the verdict rides along so a site refused on its
            // first paint (SlotShapeRefused, NotWritable) still reaches the tape. Entries accumulate in
            // _verdict until the next PaintAllTapped emits them, since emitting per chunk costs too much.
            // Side effect. A discovery's "card" record carries the later emit time (up to
            // Display.MaintenanceMs), so the tape is ordered but not precisely timestamped.
            _sites.Paint(newSites, KillsFor, _verdict);
            // Mark the chunk hot for the next HotRescanMs interval.
            long chunkStart = bufBaseAddr + lookback;
            _sweep.MarkHot(chunkStart);
        }
    }

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

    // CheckAndSnapshotCounts lives in Display.Heartbeat.cs; it also stages changed ids into the pending set.

    internal int KillsFor(int id) => _kills.TryGetValue(id, out int k) ? k : 0;
}
