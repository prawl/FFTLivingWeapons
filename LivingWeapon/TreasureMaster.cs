using System;

namespace LivingWeapon;

/// <summary>
/// Treasure Master: auto-holds bit 0x80 on each treasure tile's render-flag bytes,
/// keeping treasure tiles lit on the battlefield map while the Scholar's Ring is equipped.
///
/// Four-layer containment -- no write until ALL pass:
///   L0 build key:  dataset PE key vs live header (global disarm on mismatch).
///   L1 map id:     guarded U8 @ LiveBattleMapId, valid 1..127, present in the dataset.
///   L2 identity:   FNV-1a64 over a fixed-length terrain prefix must match the captured hash.
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

    private enum Phase { Disarmed, Arming, Armed, BattleDisarmed }

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
    private bool      _flapLoggedThisBattle = false;    // fingerprint-flap re-prove log once per battle
    private int       _ringRecheckCountdown = 0;        // ticks until the next ring re-read
    private bool      _ringEquipped = false;            // last read result from RingGate
    private bool      _ringIdleLoggedThisOffPeriod = false; // idle nag logged once per off-period

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
        _ringRecheckCountdown       = 0;     // re-read the ring immediately on the first tick
        _ringEquipped               = false;
        _ringIdleLoggedThisOffPeriod = false;
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
                    Log.Info($"treasure: dataset reloaded -- {mapCount} map(s) with addresses");
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

        // Per-tick ring countdown: refreshes _ringEquipped at most once per
        // TreasureRingRecheckTicks ticks (~1 s).  Skipped when alwaysOn (roster never read).
        if (!_alwaysOn && --_ringRecheckCountdown <= 0)
        {
            _ringRecheckCountdown = Tuning.TreasureRingRecheckTicks;
            bool wasEnabled = _ringEquipped;
            _ringEquipped = RingGate.ScholarRingEquipped(_mem);
            // Reset idle-log latch on off->on transition so each off-period logs exactly once.
            if (_ringEquipped && !wasEnabled)
                _ringIdleLoggedThisOffPeriod = false;
        }

        switch (_phase)
        {
            case Phase.Disarmed:      TickDisarmed(); break;
            case Phase.Arming:        TickArming();   break;
            case Phase.Armed:         TickArmed();    break;
            case Phase.BattleDisarmed: break;         // inert until ResetBattle
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
                Log.Info($"treasure: dataset built for game {bk.TimeDateStamp:X}/{bk.SizeOfImage:X} " +
                         $"but running {live.Value.TimeDateStamp:X}/{live.Value.SizeOfImage:X} " +
                         $"-- disarmed, re-capture needed");
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
                Log.Info($"treasure: map {mapId} {found.Name} has {found.TileCount} treasure " +
                         $"tile(s), not captured -- run treasure_flags.py session");
            }
            return;
        }

        // Live ring gate: re-read the roster on a cadence (~1 s) rather than once per battle.
        // alwaysOn bypasses the roster read entirely and always returns true.
        if (!EnabledNow()) return;

        // Has tiles + ring gate open: transition to ARMING.
        _map         = found;
        _armAttempts = 0;
        _phase       = Phase.Arming;
    }

    // ── ARMING tick ───────────────────────────────────────────────────────────────

    private void TickArming()
    {
        var map = _map!;

        // L2 fingerprint gate: skipped for map-id-only maps (water/lava: terrain animates
        // on every re-entry; no stable hash is possible across instances).
        if (!map.IsMapIdOnly)
        {
            if (!_audit.FingerprintMatches(map))
            {
                _armAttempts++;
                if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
                {
                    _capLoggedThisBattle = true;
                    var d = _audit.FingerprintDiag(map);
                    Log.Info($"treasure: map {map.MapId} fingerprint mismatch -- " +
                             $"disarmed for battle (readOk={d.ReadOk} fpVer={d.FpVer} len={d.Len} " +
                             $"got={d.Got:X} want={d.Expected:X})");
                    _phase = Phase.BattleDisarmed;
                }
                return;
            }
        }

        var (verdict, _) = _audit.AuditAddrs(map, Tuning.TreasureMinPlausibleAddrs);

        switch (verdict)
        {
            case ArmVerdict.Arm:
                _phase          = Phase.Armed;
                _revalidateTick = 0;
                Log.Info($"treasure: map {map.MapId} {map.Name} armed -- " +
                         $"{map.Tiles.Count} tile(s)" +
                         (map.IsMapIdOnly ? " (map-id-only)" : ""));
                _holder.Hold(map);
                break;

            case ArmVerdict.Retry:
                _armAttempts++;
                if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
                {
                    _capLoggedThisBattle = true;
                    Log.Info($"treasure: map {map.MapId} waiting to arm -- " +
                             $"flag bytes not in rest state (tiles off-screen?)");
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
                Log.Info($"treasure: map id changed from {map.MapId} -- resetting for new battle");
                ResetBattle();
            }
            return;   // suspend writes this tick
        }
        _badMapTicks = 0;

        // Ring gate: checked every ARMED tick (EnabledNow reads cached _ringEquipped,
        // refreshed by the per-tick countdown in Tick -- no extra roster I/O here).
        // alwaysOn=true: EnabledNow short-circuits immediately, no roster read ever.
        if (!EnabledNow())
        {
            Log.Info("treasure: Scholar's Ring removed -- treasure marks off " +
                     "(re-equip to restore)");
            _stableTicks = 0;   // force re-accumulation so DISARMED re-arms cleanly
            _phase       = Phase.Disarmed;
            _fastHold.Publish(null);
            return;
        }

        // Periodic fingerprint revalidation: skipped for map-id-only maps.
        // Water/lava maps have no fingerprint; the map-id re-check above is the only guard.
        _revalidateTick++;
        if (_revalidateTick >= Tuning.TreasureRevalidateEveryNTicks)
        {
            _revalidateTick = 0;

            if (!map.IsMapIdOnly && !_audit.FingerprintMatches(map))
            {
                // Transition back to ARMING rather than permanent BattleDisarmed.
                // TickArming will re-prove: fingerprint match + quorum re-arms normally;
                // persistent mismatch past the attempt cap -> BattleDisarmed once + log.
                _armAttempts         = 0;
                _capLoggedThisBattle = false;   // let arming re-use the cap log slot
                _phase               = Phase.Arming;
                if (!_flapLoggedThisBattle)
                {
                    _flapLoggedThisBattle = true;
                    Log.Info($"treasure: map {map.MapId} fingerprint flap -- re-proving before holding again");
                }
                return;
            }
        }

        // Hold loop: per addr, OR in 0x80 only on a Resting byte.
        // Foreign bytes (off-screen tiles: camera pan, action camera) are skipped by the
        // per-addr ClassifyAddr check inside TileHolder.Hold -- no separate veto needed.
        var (_, foreign) = _holder.Hold(map);
        if (foreign > 0 && !_foreignLoggedThisBattle)
        {
            _foreignLoggedThisBattle = true;
            Log.Info($"treasure: map {map.MapId} {foreign} byte(s) off-flag (tiles off-screen?) " +
                     $"-- skipping those, holding the rest");
        }
    }

    // ── ring gate helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the module should be active based on the Scholar's Ring gate.
    ///
    /// When <see cref="_alwaysOn"/> is true, returns true immediately; the roster is
    /// never read in this path.
    ///
    /// Otherwise reads <see cref="_ringEquipped"/>, which is refreshed by the per-tick
    /// countdown in <see cref="Tick"/> at most once per
    /// <see cref="Tuning.TreasureRingRecheckTicks"/> (~1 s).  Logs the idle message
    /// once per off-period (latch reset on the next off→on transition in Tick).
    /// </summary>
    private bool EnabledNow()
    {
        if (_alwaysOn) return true;

        if (!_ringEquipped)
        {
            if (!_ringIdleLoggedThisOffPeriod)
            {
                _ringIdleLoggedThisOffPeriod = true;
                Log.Info("treasure: no Scholar's Ring equipped -- module idle " +
                         "(equip the Scholar's Ring to enable treasure marks)");
            }
            return false;
        }
        return true;
    }
}
