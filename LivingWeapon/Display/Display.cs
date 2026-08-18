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

    // LW-165 stage 1: the timestamp of this Display's own first Tick, -1 until then. Engine only
    // starts ticking Display after the launch guard arms (Mod.cs/Engine.cs), so this stamp IS the
    // armed moment for timing purposes -- Display.PoolPaint.cs reads it to time how long the kill
    // counters took to go live after arming.
    private long _firstTickMs = -1;

    /// <param name="legends">Reliquary Phase 1's deed ledger (docs/RELIQUARY_AC.md). Null (the
    /// default) omits card-story composing entirely -- every existing caller/test that doesn't
    /// pass this behaves byte-identically to pre-Reliquary Display.</param>
    /// <param name="poolPaint">LW-37 gate: null (the default) is FALSE, the whole-heap sweep path,
    /// so every test defaults to deterministic sweep behavior INDEPENDENT of the release flag.
    /// PRODUCTION (Engine) passes Tuning.PoolPaintEnabled explicitly; a test injects true to
    /// exercise the pool-paint path.</param>
    /// <param name="recorder">LW-257: the flight-recorder tap, mirroring AttackCard's own ctor
    /// idiom (AttackCard.cs's `Action&lt;string,string&gt;? recorder` param). Null (the default,
    /// every existing test) means no flight lines are ever emitted -- see Display.Flight.cs's
    /// class doc. Production (Engine.cs) passes Flight.Record.</param>
    public Display(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory mem,
                   Func<long>? nowMs = null, LegendStore? legends = null, bool? poolPaint = null,
                   Action<string, string>? recorder = null)
    {
        _meta      = meta;
        _kills     = kills;
        _mem       = mem;
        _nowMs     = nowMs ?? (() => Environment.TickCount64);
        _pats      = new CardPatterns(meta);
        _poolPaint   = poolPaint ?? false;
        _poolLocator = new PoolLocator(mem, _pats, _nowMs);
        _sweep     = new DisplaySweep(mem, _nowMs);
        // StoryLines owns EarnedAnchors (the three-way anchor registry, decision 2) -- built
        // before CardSites so CardSites can be handed its anchors at construction. SeedAtStartup
        // recomposes every weapon's CURRENT line from persisted deed state and loads PREVIOUS
        // from the store's "lastPainted", so the very first paint already carries earned lines.
        _stories   = legends != null ? new StoryLines(legends, meta, kills, _pats) : null;
        _stories?.SeedAtStartup();
        // LW-262: the cap-relief prune's own eviction tap, Display.Flight.cs's OnSitePruned.
        // Wiring the method group here is safe even though _recorder is assigned further below
        // (OnSitePruned only reads _recorder when actually INVOKED, at a future PruneDeadSites
        // call, by which point this constructor has long since finished).
        _sites     = new CardSites(mem, _pats, _stories?.Anchors, OnSitePruned);
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

        // LW-257: Display.Flight.cs owns everything else about this tap. _verdict is left null
        // (no allocation) when no recorder is wired, so the common no-recorder case (every
        // existing test) never sees this arc's ledger bookkeeping at all.
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
        _flightBudget = 0;     // LW-257: a new coverage/cache window gets a fresh record budget
        _coverageBudget = 0;   // LW-257 (F3): its own reserve, reset on the same window boundary
        _locateFlightBudget = 0;   // LW-261: the locate-complete tap's own reserve, same window boundary
        _evictedBudget = 0;    // LW-259: tier 1's own reserve, reset on the same window boundary
        _pendingIds.Clear();   // LW-257 commit 2 (round 2 review): _sites is wiped above, so every
                               // pending id's watch is against a cache that no longer exists --
                               // without this it burns all CardPendingMaxBeats watching nothing
                               // and logs a false "gave up" line for a kill that was never lost.
    }

    /// <summary>LW-91 stage 2: a narrow, in-battle-safe repaint driven by a kill-count change
    /// alone. Engine calls this on the in-battle ticks ShouldPaintCard skips (the paused-status-
    /// card / post-battle-settle gate), so a kill landing mid-fight still reaches the equip card
    /// instead of waiting for the settle window. Mirrors the count-change edge inside <see
    /// cref="Tick"/> byte-for-byte -- Recompose BEFORE PaintAll (Display.cs's own Tick ordering
    /// above), since this narrow path is the ONLY place that consumes the shared _lastCounts edge
    /// this tick; if it fired the two out of order, a story-line rotation would render one call
    /// stale for whatever painted here. RequestRescan latches the sweep's next full Tick onto a
    /// hot re-offer rather than stepping the sweep itself: still no sweep stepping and no
    /// Invalidate call FROM THIS METHOD. LW-261 correction: the pool relocate itself is no longer
    /// something only the full Tick can trigger -- Engine's own "pool-locate" tick lane
    /// (Engine.cs, TickGates.Always) steps PoolLocator.Step (Display.PoolLocate.cs's
    /// StepPoolLocate) on EVERY tick, independent of whether Tick or this narrow method runs this
    /// particular tick, so a relocate this method itself never asked for can still be quietly
    /// making progress, or even publish, in the background while the player stays on-field.
    ///
    /// LW-257 commit 2 (CORRECTED -- this doc previously promised "no pool/sweep locate work ...
    /// those stay exclusively inside the full Tick" with no qualifier at all, which this commit
    /// deliberately makes untrue): on a call where nothing above already painted, this method now
    /// ALSO reaches the shared maintenance beat (MaintenanceDue/RunMaintenance, Display.
    /// Heartbeat.cs) that used to live only inside Tick -- the on-field path needed that same
    /// once-a-second repaint discipline just as much as the off-field path already had (see
    /// Display.Heartbeat.cs's own class doc for what the beat actually does; it is a settlement
    /// watchdog, not a retry loop -- no extra paint work follows from anything being pending).
    /// That beat CAN, in turn, issue a targeted single-region ScanPoolRegion re-offer when a
    /// located pool region's kills-site count has drained below its latch (Display.PoolDrain.cs's
    /// ReOfferDrainedRegions) -- real pool-scanning work, just never a full relocate, never a
    /// Regions() walk, and never the whole-heap sweep FROM THIS METHOD ITSELF. One honest caveat
    /// (round 2 review): that re-offer can still flip _poolCovered to false when a whole region is
    /// genuinely gone (Display.PoolDrain.cs's own class doc), and that flag only SCHEDULES a
    /// future full relocate -- unlike Tick, nothing on this call path runs MaybePoolPaint
    /// afterward to act on it, so on the on-field path the flag just sits until Engine's paint
    /// phase next takes the Tick(true) branch (BattleState.ShouldPaintCard, Engine.cs).</summary>
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

        // Maintenance beat, shared with Tick (Display.Heartbeat.cs). Skipped when the branch
        // above already painted this call -- mirrors Tick's own "skip the maintenance paint when
        // a count/target change already painted this tick" gating exactly, just without a
        // targetsChanged term (this narrow path never touches BuildTargets/_lastTargets at all).
        if (MaintenanceDue(_nowMs()) && !countsChanged)
            RunMaintenance();
    }

    /// <summary>Drive one display cycle. <paramref name="inBattle"/> true shrinks the byte
    /// budget to avoid competing with the kill-poll path during a live fight.</summary>
    public void Tick(bool inBattle)
    {
        if (_firstTickMs < 0) _firstTickMs = _nowMs();

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
            PaintAllTapped();
        }

        // Maintenance repaint: PaintAll on a clock cadence (shared with PaintCountsIfChanged,
        // Display.Heartbeat.cs's MaintenanceDue/RunMaintenance) to drain dead sites, refresh any
        // stale on-screen copy, and check whether any still-pending id has settled yet (a
        // settlement watchdog, not a retry -- Display.Heartbeat.cs's own class doc: this same
        // PaintAll runs regardless of what is pending, so nothing extra is attempted on its
        // account), without waiting for a kill-count change. skip-if-equal keeps steady-state
        // writes at zero; this is cheap in the common case. MaintenanceDue ALWAYS advances the
        // shared clock when due, even on the branch below that then skips the actual paint --
        // see that method's own doc.
        if (MaintenanceDue(_nowMs()) && !countsChanged && !targetsChanged)
            RunMaintenance();

        // Target change means fresh card buffers may have appeared; start a new generation
        // after the min-gap floor rather than waiting up to GenerationRestMs (90s).
        if (targetsChanged)
            _sweep.Invalidate();

        _lastTargets = targets;

        if (!(_poolPaint && MaybePoolPaint()))
        {
            long budget = inBattle ? BudgetInBattle : BudgetOutOfBattle;
            // allSuffixes stays false here (LW-59): the 33ms engine loop's per-chunk perf
            // contract is unchanged for the whole-heap sweep, pinned by
            // DisplayPoolPaintTests.Sweep_path_still_limits_suffix_search_to_targets_plus_rotation.
            _sweep.Tick(budget, (buf, lookback, searchable, bufBaseAddr) => OnChunk(buf, lookback, searchable, bufBaseAddr, allSuffixes: false));
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
    /// Called by the sweep (or the pool path, Display.PoolPaint.cs) for every offered chunk.
    /// Discovers kills and suffix sites, registers them, then paints ONLY the newly-registered
    /// sites (not the full cache) to avoid an O(all-sites) verify storm on every hit chunk.
    /// PaintAll is reserved for the count-change path in Tick where a known stale value must
    /// be pushed everywhere.
    ///
    /// Suffix coverage: the target set is always included. When <paramref name="allSuffixes"/>
    /// is false (the whole-heap sweep), a rotation slice of ids that had kills hits in this
    /// chunk is added on top (see <see cref="SuffixRotation"/> for the per-ID coverage-cycle
    /// policy) so successive chunks and passes cycle through all ids, no chunk has to wait for
    /// a new generation. When true (the pool path, LW-59), every tracked id is searched instead
    /// of just the rotation slice: the pool's suffix bytes can go stale independent of the
    /// tally reset (SuffixRotation's own coverage set survives Display.Invalidate, by design,
    /// so a rotation slice cannot be trusted to heal every id within one reset), and painting
    /// one small pool chunk is cheap relative to the whole-heap sweep it replaces.
    /// </summary>
    private void OnChunk(byte[] buf, int lookback, int searchable, long bufBaseAddr, bool allSuffixes)
    {
        var killsHits = new List<CardScanner.KillsHit>();
        CardScanner.FindKills(buf, lookback, searchable, _pats, killsHits, _stories?.Anchors);

        // Gather ids that got kills hits in this chunk for the rotation slice.
        var hitIds = new HashSet<int>();
        foreach (var h in killsHits) hitIds.Add(h.Id);

        // Suffix pass: targets UNION either every tracked id (pool path) or a rotation slice
        // of ids that had kills hits (sweep path).
        var suffixIds = new HashSet<int>(_lastTargets);
        if (hitIds.Count > 0)
        {
            if (allSuffixes) suffixIds.UnionWith(_meta.Keys);
            else suffixIds.UnionWith(_rotation.Take(hitIds, _lastTargets));
        }

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
            // Paint only the new sites from this chunk (not the full 512-site cache). LW-257
            // (round-3 review, F4): the verdict rides along too, so a freshly-discovered site
            // refused on its FIRST paint (SlotShapeRefused, NotWritable) reaches the tape instead
            // of vanishing silently -- before this, only PaintAllTapped's PaintAll fed the ledger,
            // so a site that never survives past discovery had no trace at all. Deliberately "let
            // it ride" rather than emit here: entries accumulate in the shared _verdict across
            // every OnChunk call (this can run many times in a single Tick, once per offered
            // chunk) until the next PaintAllTapped's EmitVerdict+Clear -- emitting per chunk would
            // multiply log-formatting cost far past the cadence this arc's cost budget accepts.
            // Bounded the same way every other Note() caller already is (CardVerdict's own
            // 256-entry notable cap); nothing here is a new unbounded-growth risk.
            // DECLARED OMISSION (round-4 review, item 4, same declare-the-gap rule the spec
            // applied to the dropped `from=` field): letting it ride costs TIMESTAMP ACCURACY,
            // not just delay. Flight.Record stamps a record when Flight.Record is actually
            // CALLED, so a discovery outcome noted here is emitted later, inside the next
            // PaintAllTapped's EmitVerdict -- its "card" record therefore carries THAT LATER
            // moment's clock time, up to about a second after (Display.MaintenanceMs) the site
            // was actually discovered, not this OnChunk call's own timestamp. Fine for what this
            // ledger is used for (a diagnostic ordering, not a precise event log), but a reader
            // correlating this tape against another system's timestamps should know the skew exists.
            _sites.Paint(newSites, KillsFor, _verdict);
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

    // CheckAndSnapshotCounts moved to Display.Heartbeat.cs (LW-257 commit 2): its body now also
    // stages changed ids into the pending set that file owns.

    internal int KillsFor(int id) => _kills.TryGetValue(id, out int k) ? k : 0;
}
