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

    /// <summary>Cadence for the maintenance PaintAll call that drains dead sites and
    /// repaints any stale on-screen copy without waiting for a kill-count change.</summary>
    internal const long MaintenanceMs = 1000;

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int>        _kills;
    private readonly IGameMemory                 _mem;
    private  readonly CardPatterns                _pats;  // card-pattern set consumed by Display painting
    internal readonly DisplaySweep                _sweep;  // DisplayPoolPaintTests reads Generation/IsComplete (LW-37 skip proof)
    internal readonly CardSites                  _sites;  // DisplayMaintenanceTests reads Count
    private readonly WpScratchPainter            _wpScratch;
    private readonly Func<long>                  _nowMs;
    // Reliquary Phase 1 (docs/RELIQUARY_AC.md): the card-story compose driver. Null when no
    // LegendStore is wired (every pre-Reliquary caller/test) -- CardSites/CardScanner then fall
    // back to baked-only anchor verification, byte-identical to before. Internal so integration
    // tests can drive/inspect it directly (mirrors _pats/_sites's own test-accessor convention).
    internal readonly StoryLines? _stories;

    private readonly Dictionary<int, int> _lastCounts = new();
    private readonly SuffixRotation       _rotation = new();
    private HashSet<int> _lastTargets = new();

    // Timestamp of the last maintenance PaintAll; initialised to -1 (before any real clock
    // value) so the first Tick always triggers the maintenance pass.  Using long.MinValue
    // would overflow on the subtraction `now - _lastMaintenanceMs` since now >= 0.
    private long _lastMaintenanceMs = -1;

    // Generation number at the last log line so we log once per completion.
    private long _lastLoggedGen = -1;

    /// <param name="legends">Reliquary Phase 1's deed ledger (docs/RELIQUARY_AC.md). Null (the
    /// default) omits card-story composing entirely -- every existing caller/test that doesn't
    /// pass this behaves byte-identically to pre-Reliquary Display.</param>
    /// <param name="poolPaint">LW-37 gate: null (the default) is FALSE, the whole-heap sweep path,
    /// so every test defaults to deterministic sweep behavior INDEPENDENT of the release flag.
    /// PRODUCTION (Engine) passes Tuning.PoolPaintEnabled explicitly; a test injects true to
    /// exercise the pool-paint path.</param>
    public Display(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory mem,
                   Func<long>? nowMs = null, LegendStore? legends = null, bool? poolPaint = null)
    {
        _meta      = meta;
        _kills     = kills;
        _mem       = mem;
        _nowMs     = nowMs ?? (() => Environment.TickCount64);
        _pats      = new CardPatterns(meta);
        _poolPaint   = poolPaint ?? false;
        _poolLocator = new PoolLocator(mem, _pats);
        _sweep     = new DisplaySweep(mem, _nowMs);
        // StoryLines owns EarnedAnchors (the three-way anchor registry, decision 12) -- built
        // before CardSites so CardSites can be handed its anchors at construction. SeedAtStartup
        // recomposes every weapon's CURRENT line from persisted deed state and loads PREVIOUS
        // from the store's "lastPainted", so the very first paint already carries earned lines.
        _stories   = legends != null ? new StoryLines(legends, meta, kills, _pats) : null;
        _stories?.SeedAtStartup();
        _sites     = new CardSites(mem, _pats, _stories?.Anchors);
        _wpScratch = new WpScratchPainter(mem, meta, KillsFor);

        // Startup invariant: the sweep's lookback prefix must hold the longest anchor plus
        // the widest painted slot, or a card straddling a chunk boundary could surface its
        // slot with the anchor cut off and never verify. Log-and-continue -- a too-long
        // name only degrades painting, while a throw here would take the game with it.
        // Reliquary note: this bound stays valid even once earned lines are registered --
        // EarnedAnchors enforces every earned pattern's encoded byte length EQUAL to its
        // weapon's baked Flavor pattern (CardPatterns.MaxAnchorLen is computed from baked
        // Name/Flavor only), so an earned anchor can never exceed MaxAnchorLen
        // (DisplayLookbackInvariantTests.FitsLookback_with_earned_patterns_registered).
        if (!_pats.FitsLookback(DisplaySweep.Lookback))
        {
            ModLogger.Error(LogVerb.Display, "The equip-card painter is misconfigured and may fail to paint some kill counters.");
            ModLogger.Debug(LogVerb.Trace, "painter misconfiguration detail (lookback="
                      + DisplaySweep.Lookback + " < maxAnchor=" + _pats.MaxAnchorLen + " + slot)");
        }

        // Forward twin of the check above: the bidirectional attribution search (CardScanner) can
        // find a weapon's flavor AFTER its "Kills: " hit (the new deployed layout), so the
        // trailing slack must also fit the widest anchor + literal + slot. Same log-and-continue
        // posture: a too-short slack only drops boundary-straddling cards, never crashes.
        if (!_pats.FitsTrailSlack(DisplaySweep.TrailSlack))
        {
            ModLogger.Error(LogVerb.Display, "The equip-card painter is misconfigured and may fail to paint kill counters near a chunk boundary.");
            ModLogger.Debug(LogVerb.Trace, "painter misconfiguration detail (trailSlack="
                      + DisplaySweep.TrailSlack + " < maxAnchor=" + _pats.MaxAnchorLen + " + slot)");
        }
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
        // also trigger a rescan when their kill count changes. changedIds feeds StoryLines'
        // recompose (Reliquary Phase 1) -- a deed can only change alongside a tally increment
        // (KillTracker.CreditKill), so this is exactly the compose-change edge.
        var changedIds = new List<int>();
        bool countsChanged = CheckAndSnapshotCounts(changedIds);
        bool targetsChanged = !targets.SetEquals(_lastTargets);

        // Recompose BEFORE painting so this same Tick's PaintAll repaint-through (CardSites)
        // sees the fresh current line immediately, rather than lagging a tick behind.
        if (countsChanged) _stories?.RecomposeChanged(changedIds);

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

        if (!(_poolPaint && MaybePoolPaint()))
        {
            long budget = inBattle ? BudgetInBattle : BudgetOutOfBattle;
            _sweep.Tick(budget, OnChunk);
        }

        // Log once per generation completion so the log captures each full scan. Generation 1
        // is the per-launch liveness canary and reaches the console at Info; every later
        // generation stays Debug (file-only): the release checklists that used to cite the
        // console line now grep the file instead.
        if (_sweep.IsComplete && _sweep.Generation != _lastLoggedGen)
        {
            _lastLoggedGen = _sweep.Generation;
            if (_sweep.Generation == 1)
                ModLogger.Event(LogVerb.Display, $"The card display sweep completed its first pass, maintaining {_sites.Count} card-text spots.");
            else
                ModLogger.Debug(LogVerb.Display, "memory sweep number " + _sweep.Generation + " finished; maintaining " + _sites.Count + " card-text spots");
        }

        // WpScratch: keyed by the mirror weapon (the card on screen), NOT roster slot 0.
        // Roster-slot-0 keying painted Ramza's boost while viewing any other unit.
        _wpScratch.Paint();
    }

    // ─── private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Called by the sweep for every offered chunk.  Discovers kills and suffix sites,
    /// registers them, then paints ONLY the newly-registered sites (not the full cache)
    /// to avoid an O(all-sites) verify storm on every hit chunk.  PaintAll is reserved
    /// for the count-change path in Tick where a known stale value must be pushed everywhere.
    ///
    /// Suffix coverage: the target set is always included; a rotation slice of ids that had
    /// kills hits in this chunk is added on top (see <see cref="SuffixRotation"/> for the
    /// per-ID coverage-cycle policy) so successive chunks and passes cycle through all ids
    /// -- no chunk has to wait for a new generation.
    /// </summary>
    private void OnChunk(byte[] buf, int lookback, int searchable, long bufBaseAddr)
    {
        var killsHits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, _pats, killsHits, _stories?.Anchors);

        // Gather ids that got kills hits in this chunk for the rotation slice.
        var hitIds = new HashSet<int>();
        foreach (var h in killsHits) hitIds.Add(h.Id);

        // Suffix pass: targets UNION a rotation slice of ids that had kills hits.
        var suffixIds = new HashSet<int>(_lastTargets);
        if (hitIds.Count > 0)
            suffixIds.UnionWith(_rotation.Take(hitIds, _lastTargets));

        var suffixHits = new List<CardScanner.SuffixHit>();
        CardScanner.FindSuffixes(buf, lookback, searchable, _pats, suffixIds, suffixHits);

        // Collect newly-registered sites so we paint only those, not the whole cache.
        var newSites = new List<CardSites.Site>();

        foreach (var h in killsHits)
        {
            long slotAddr   = bufBaseAddr + h.SlotPos;
            long anchorAddr = h.FlavorPos >= 0 ? bufBaseAddr + h.FlavorPos : slotAddr;
            var site = new CardSites.Site(h.Id, h.Enc, slotAddr, anchorAddr, IsKills: true);
            if (_sites.Add(site)) newSites.Add(site);
        }

        foreach (var h in suffixHits)
        {
            long slotAddr   = bufBaseAddr + h.SlotPos;
            long anchorAddr = bufBaseAddr + h.NamePos;
            var site = new CardSites.Site(h.Id, h.Enc, slotAddr, anchorAddr, IsKills: false);
            if (_sites.Add(site)) newSites.Add(site);
        }

        if (newSites.Count > 0)
        {
            // Paint only the new sites from this chunk (not the full 512-site cache).
            _sites.Paint(newSites, KillsFor);
            // Mark chunk as hot so it gets priority on the next HotRescanMs interval.
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

    /// <summary>Compare current kill counts against the last snapshot for all tracked ids.
    /// Updates the snapshot on any change, appending each changed id to <paramref name="changedIds"/>
    /// (Reliquary Phase 1's StoryLines.RecomposeChanged input). Returns true if any count changed.</summary>
    private bool CheckAndSnapshotCounts(List<int> changedIds)
    {
        bool changed = false;
        foreach (int id in _meta.Keys)
        {
            int cur = KillsFor(id);
            _lastCounts.TryGetValue(id, out int last);
            if (cur != last) { _lastCounts[id] = cur; changed = true; changedIds.Add(id); }
        }
        return changed;
    }

    internal int KillsFor(int id) => _kills.TryGetValue(id, out int k) ? k : 0;
}
