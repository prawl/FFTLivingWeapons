using System;

namespace LivingWeapon;

/// <summary>
/// Treasure Master: auto-holds bit 0x80 on each treasure tile's render-flag bytes,
/// keeping treasure tiles lit on the battlefield map while the Scholar's Ring is equipped.
///
/// Containment -- L0/L1/L3 gate writes; L2 is ADVISORY (LIVE INCIDENT #5):
///   L0 build key:  dataset PE key vs live header (global disarm on mismatch).
///   L1 map id:     guarded U8 @ LiveBattleMapId, valid 1..127, present in the dataset.
///   L2 identity:   ADVISORY ONLY -- FNV-1a64 over the terrain prefix is logged on mismatch
///                  (per-battle weather perturbs the hashed fields) but never blocks arming.
///   L3 per-write:  per-addr Writable guard + ClassifyAddr check; Foreign bytes (off-screen
///                  render bytes from camera pan / action camera) are never written. At arm
///                  time, a quorum of TreasureMinPlausibleAddrs ok addrs is required; below
///                  quorum the module polls indefinitely rather than disarming permanently.
///
/// OR-only by construction: the module has no Clear path. Release = stop writing; the
/// engine clears marks itself (LIVE_LEDGER). Writes are CharmLock.Force-idiom: Writable
/// guard -> U8 read -> write cur|0x80 only on difference.
///
/// The stateless gate evaluations (PE key, fingerprint, per-addr audit) live in ArmAudit.cs
/// (a separate class -- a partial would be same-state-machine evasion per the house rules).
/// The pure policy statics (ClassifyAddr, Fnv1a64, DecideArm, etc.) live in TreasureMaster.Policy.cs.
/// </summary>
internal sealed partial class TreasureMaster : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    // ── internal state ────────────────────────────────────────────────────────────

    private enum Phase { Disarmed, Arming, Armed }

    private TreasureDb            _db;
    private readonly Func<TreasureDb>    _load;
    private readonly Func<DateTime?>     _datasetStamp;
    private readonly IGameMemory  _mem;
    private readonly ArmAudit     _audit;
    private readonly TileHolder   _holder;

    private readonly bool _alwaysOn;
    private readonly FastHold _fastHold;

    private Phase     _phase          = Phase.Disarmed;
    private TreasureMap? _map         = null;    // the active map, null when none found
    private int       _stableTicks    = 0;       // consecutive ticks with the same valid map id
    private byte      _stableMapId    = 0;
    private int       _armAttempts    = 0;
    private int       _revalidateTick = 0;
    private int       _badMapTicks    = 0;       // consecutive bad-map-id ticks while ARMED
    private bool      _globalIdle     = false;   // permanent disarm set at first tick
    private bool      _globalIdleChecked = false;
    private bool      _naggedThisBattle  = false; // stub/missing nag once per battle
    private bool      _capLoggedThisBattle = false;
    private bool      _foreignLoggedThisBattle = false; // armed off-screen log once per battle
    private bool      _flapLoggedThisBattle = false;    // fingerprint-mismatch log once per battle (arm or mid-battle)
    private bool      _enabledThisBattle = false;       // cached ring-gate result (cached only once TRUE)
    private bool      _ringIdleLoggedThisBattle = false; // idle nag logged once per battle

    private int       _stampCheckCountdown = 0;   // ticks until the next stamp comparison
    private DateTime? _lastStamp = null;           // stamp seen at the last check (null = not yet checked)
    private bool      _stampInitialized = false;   // false until the first load has run

    /// <param name="alwaysOn">Override for the AlwaysOn gate; null = use Tuning.TreasureAlwaysOn.
    /// Tests pass true directly so the ring-gate branch does not idle the module in prod builds.</param>
    public TreasureMaster(TreasureDb db, IGameMemory? mem = null, bool? alwaysOn = null)
        : this(load: () => db, datasetStamp: () => null, mem: mem, alwaysOn: alwaysOn) { }

    /// <summary>
    /// Injectable seam ctor used by tests and Engine alike.
    /// <paramref name="load"/> is called eagerly on the first Tick and again whenever
    /// <paramref name="datasetStamp"/> returns a value that differs from the last seen stamp.
    /// The stamp returning null is treated as "unchanged" (no-reload).
    /// </summary>
    public TreasureMaster(
        Func<TreasureDb> load,
        Func<DateTime?> datasetStamp,
        IGameMemory? mem = null,
        bool? alwaysOn = null)
    {
        _load         = load;
        _datasetStamp = datasetStamp;
        _db           = TreasureDb.MakeEmpty();   // placeholder; replaced on first Tick
        _mem          = mem ?? new LiveMemory();
        _audit        = new ArmAudit(_mem);
        _holder       = new TileHolder(_mem);
        _alwaysOn     = alwaysOn ?? Tuning.TreasureAlwaysOn;
        _fastHold     = new FastHold(_holder, Tuning.TreasureFastHoldMs);
        // Thread not started here -- tests must not spawn OS threads; Engine calls StartFastHold().
    }

    /// <summary>Starts the fast-hold background thread. Called by Engine after construction.</summary>
    public void StartFastHold() => _fastHold.Start();

    /// <summary>Exposes the fast-hold instance so tests can drive HoldOnce without a thread.</summary>
    internal FastHold FastHold => _fastHold;

    // ── ISignature ────────────────────────────────────────────────────────────────

    public void ResetBattle()
    {
        _phase           = Phase.Disarmed;
        _map             = null;
        _stableTicks     = 0;
        _stableMapId     = 0;
        _armAttempts     = 0;
        _revalidateTick  = 0;
        _badMapTicks     = 0;
        _naggedThisBattle           = false;
        _capLoggedThisBattle        = false;
        _foreignLoggedThisBattle    = false;
        _flapLoggedThisBattle       = false;
        _enabledThisBattle          = false;
        _ringIdleLoggedThisBattle   = false;
        // _globalIdle and _globalIdleChecked persist -- the L0 check is startup-once.
        _fastHold.Publish(null);    // immediacy on battle-exit: stop holding before Tick runs again
    }

    // ── entry point ───────────────────────────────────────────────────────────────

    public void Tick(DateTime now, bool inLive)
    {
        // Eager initial load + periodic stamp-change hot-reload (runs regardless of phase or inLive).
        if (!_stampInitialized)
        {
            _db               = _load();
            _stampInitialized = true;
            _lastStamp        = _datasetStamp();
            _stampCheckCountdown = Tuning.TreasureStampCheckTicks;
        }
        else
        {
            if (--_stampCheckCountdown <= 0)
            {
                _stampCheckCountdown = Tuning.TreasureStampCheckTicks;
                var current = _datasetStamp();
                if (current.HasValue && current != _lastStamp)
                {
                    _lastStamp = current;
                    _db = _load();
                    // Full state reset so L0 re-evaluates against the new dataset.
                    _globalIdle        = false;
                    _globalIdleChecked = false;
                    ResetBattle();
                    var mapCount = 0;
                    foreach (var m in _db.Maps)
                        if (m.Tiles.Count > 0) mapCount++;
                    ModLogger.Debug(LogVerb.Treasure, $"reloaded the treasure dataset; {mapCount} map(s) with addresses");
                }
            }
        }

        // L0: one-time global idle check (dataset empty, AlwaysOn false, or key mismatch).
        // If CheckGlobalIdle defers (PE header not yet readable), _globalIdleChecked resets
        // to false -- return immediately so the phase switch cannot run before L0 resolves.
        if (!_globalIdleChecked)
        {
            CheckGlobalIdle();
            if (!_globalIdleChecked) { _fastHold.Publish(null); return; }
        }
        if (_globalIdle) { _fastHold.Publish(null); return; }

        if (!inLive)
        {
            _stableTicks = 0;   // stability counter resets when not in live battle
            _fastHold.Publish(null);
            return;
        }

        switch (_phase)
        {
            case Phase.Disarmed:      TickDisarmed(); break;
            case Phase.Arming:        TickArming();   break;
            case Phase.Armed:         TickArmed();    break;
        }

        // Single publish point: hold exactly when phase==Armed AND inLive.
        _fastHold.Publish(_phase == Phase.Armed ? _map : null);
    }

    // ── L0 global-idle check (once at first tick) ─────────────────────────────────

    private void CheckGlobalIdle()
    {
        _globalIdleChecked = true;

        if (_db.Maps.Count == 0)
        {
            _globalIdle = true;
            return;   // silent: no dataset, no output
        }

        // If the dataset has a build key, compare it to the live PE header.
        if (_db.BuildKey is { } bk)
        {
            var live = _audit.ReadPeBuildKey();
            if (live is null)
            {
                // Can't read the header yet -- don't mark global idle, retry next tick.
                _globalIdleChecked = false;
                return;
            }
            if (!BuildKeyMatches(
                    (uint)bk.TimeDateStamp, (uint)bk.SizeOfImage,
                    live.Value.TimeDateStamp, live.Value.SizeOfImage))
            {
                _globalIdle = true;
                // Console Warn only when a Ring bearer could actually be affected; without a
                // ring in the roster (and no AlwaysOn override) the warning demotes to the file.
                ModLogger.For(LogVerb.Treasure, () => _alwaysOn || RingGate.ScholarRingInRoster(_mem))
                    .Warn("Treasure marks are disarmed: the dataset was built for a different game build (re-capture needed).");
                ModLogger.Debug(LogVerb.Trace, $"treasure build-key detail (dataset {bk.TimeDateStamp:X}/{bk.SizeOfImage:X}, running {live.Value.TimeDateStamp:X}/{live.Value.SizeOfImage:X})");
                return;
            }
        }
        // Build key null (stub-only dataset) or matches: proceed.
    }

    // ── DISARMED tick ─────────────────────────────────────────────────────────────

    private void TickDisarmed()
    {
        if (!_audit.TryReadMapId(out byte mapId)) { _stableTicks = 0; return; }

        if (mapId == _stableMapId)
            _stableTicks++;
        else
        {
            _stableMapId = mapId;
            _stableTicks = 1;
        }

        if (_stableTicks < Tuning.TreasureArmStableTicks) return;

        // Stable: look up the db.
        TreasureMap? found = null;
        foreach (var m in _db.Maps)
        {
            if (m.MapId == mapId) { found = m; break; }
        }

        if (found is null) return;   // unknown map: silent

        if (found.Tiles.Count == 0)
        {
            // Stub (no tiles) -- nag once per battle.
            if (!_naggedThisBattle)
            {
                _naggedThisBattle = true;
                ModLogger.Debug(LogVerb.Treasure, $"skipped map {found.Name}: {found.TileCount} treasure " +
                         $"tile(s) exist but are not captured; run the treasure_flags.py session (map id {mapId})");
            }
            return;
        }

        // Per-battle ring gate: re-evaluate each disarmed tick until enabled, then cache for the
        // battle. Re-checking (not caching the first result) is load-bearing: on a fresh battle
        // LOAD the map id stabilises before the live band finishes populating, so caching a
        // premature "no" stranded a deployed ring-bearer's marks for the whole battle (live
        // 2026-06-12). Once enabled the result sticks, so a mid-battle unequip doesn't drop marks.
        // alwaysOn bypasses the roster/band read entirely (force-on override).
        if (!_enabledThisBattle)
        {
            _enabledThisBattle = _alwaysOn || RingGate.ScholarRingEquipped(_mem);
            if (!_enabledThisBattle)
            {
                // Nag once ONLY when there's no ring in the party at all. A ring on a benched
                // unit -- or one whose band entry is still loading -- stays silent, not a "no
                // ring" error (the band cross-check just hasn't found a deployed bearer yet).
                if (!_ringIdleLoggedThisBattle && !_alwaysOn && !RingGate.ScholarRingInRoster(_mem))
                {
                    _ringIdleLoggedThisBattle = true;
                    // File-only: an unarmed module never speaks on console (log-facelift ruling).
                    ModLogger.Debug(LogVerb.Treasure, "idle this battle: no Scholar's Ring is equipped " +
                             "(equip one on a unit in this battle to enable treasure marks)");
                }
                return;
            }
        }

        // Has tiles + ring gate open: transition to ARMING.
        _map         = found;
        _armAttempts = 0;
        _phase       = Phase.Arming;
    }

    // ── ARMING tick ───────────────────────────────────────────────────────────────

    private void TickArming()
    {
        var map = _map!;

        // Advisory fingerprint check -- TELEMETRY ONLY, never blocks arming (LIVE INCIDENT #5).
        // Terrain fingerprints proved unreliable as a gate: per-battle weather (rain) perturbs
        // the hashed terrain fields, so a map captured in one weather state fails to match in
        // another -- and there is no data to know which maps can weather. Containment is carried
        // by the build key (L0), the per-tick map-id match (L1, unique per map) and the per-tile
        // resting-byte audit + quorum below (L3). A mismatch is logged once per battle as a drift
        // census but does NOT disarm. Map-id-only maps have no fingerprint to check.
        if (!map.IsMapIdOnly && !_flapLoggedThisBattle && !_audit.FingerprintMatches(map))
        {
            _flapLoggedThisBattle = true;
            var d = _audit.FingerprintDiag(map);
            ModLogger.WarnWithTrace(LogVerb.Treasure,
                $"The terrain on map {map.Name} looks different than expected (likely weather); arming the treasure marks anyway.",
                $"treasure fingerprint detail (map id {map.MapId}, readOk={d.ReadOk} fpVer={d.FpVer} got={d.Got:X} want={d.Expected:X})");
        }

        var (verdict, _) = _audit.AuditAddrs(map, Tuning.TreasureMinPlausibleAddrs);

        switch (verdict)
        {
            case ArmVerdict.Arm:
                _phase          = Phase.Armed;
                _revalidateTick = 0;
                ModLogger.EventWithTrace(LogVerb.Treasure,
                    $"Treasure marks are armed on map {map.Name}: {map.Tiles.Count} tile(s).",
                    $"treasure arm detail (map id {map.MapId}{(map.IsMapIdOnly ? ", map-id-only" : "")})");
                _holder.Hold(map);
                break;

            case ArmVerdict.Retry:
                _armAttempts++;
                if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
                {
                    _capLoggedThisBattle = true;
                    ModLogger.WarnWithTrace(LogVerb.Treasure,
                        $"Treasure marks are still waiting to arm on map {map.Name}; the flag bytes are not yet at rest (tiles off-screen?).",
                        $"treasure arm-wait detail (map id {map.MapId}, attempts {_armAttempts})");
                }
                break;
        }
    }

    // ── ARMED tick ────────────────────────────────────────────────────────────────

    private void TickArmed()
    {
        var map = _map!;

        // Re-check map id every tick (single read -- two reads can disagree mid-frame).
        bool mapOk = _audit.TryReadMapId(out byte currentMapId) && currentMapId == map.MapId;

        if (!mapOk)
        {
            _badMapTicks++;
            if (_badMapTicks >= Tuning.TreasureMapIdBadTicksToReset)
            {
                // Map changed (chained battle) or something went wrong -- full reset.
                ModLogger.Debug(LogVerb.Treasure, $"reset treasure for a new battle; the map id changed (from {map.MapId})");
                ResetBattle();
            }
            return;   // suspend writes this tick
        }
        _badMapTicks = 0;

        // Mid-battle fingerprint check -- INFORMATIONAL ONLY (skipped for map-id-only maps,
        // and once the first drift has been logged this battle).
        //
        // Terrain identity was already proven at ARM time (TickArming); the per-tick map-id
        // re-check above is the live "still in this battle" guard, and the per-addr ClassifyAddr
        // gate in the hold loop backstops stray writes. A fingerprinted map whose "static"
        // terrain fields drift mid-battle (LIVE INCIDENT #4: Siedge Weald map 74 -- fields
        // {2,3,4,5} changed ~26 s into the SAME battle) must NOT disarm: the old behavior
        // dropped to ARMING, stopped holding, and -- when the new terrain state persisted --
        // permanently disarmed for the battle, killing the marks for the rest of the fight. We
        // log the first drift per battle (a free in-the-wild drift census) and keep holding.
        if (!map.IsMapIdOnly && !_flapLoggedThisBattle
                && ++_revalidateTick >= Tuning.TreasureRevalidateEveryNTicks)
        {
            _revalidateTick = 0;
            if (!_audit.FingerprintMatches(map))
            {
                _flapLoggedThisBattle = true;
                ModLogger.WarnWithTrace(LogVerb.Treasure,
                    $"The terrain on map {map.Name} drifted mid-battle; the treasure marks are held through it, not disarmed.",
                    $"treasure drift detail (map id {map.MapId}, fingerprint no longer matches)");
            }
        }

        // Hold loop: per addr, OR in 0x80 only on a Resting byte.
        // Foreign bytes (off-screen tiles: camera pan, action camera) are skipped by the
        // per-addr ClassifyAddr check inside TileHolder.Hold -- no separate veto needed.
        var (_, foreign) = _holder.Hold(map);
        if (foreign > 0 && !_foreignLoggedThisBattle)
        {
            _foreignLoggedThisBattle = true;
            ModLogger.WarnWithTrace(LogVerb.Treasure,
                $"Skipped {foreign} treasure tile(s) that look off-screen on map {map.Name}; holding the rest.",
                $"treasure off-screen detail (map id {map.MapId})");
        }
    }
}
