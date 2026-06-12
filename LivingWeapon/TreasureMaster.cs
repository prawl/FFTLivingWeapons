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

    private readonly TreasureDb   _db;
    private readonly IGameMemory  _mem;
    private readonly ArmAudit     _audit;
    private readonly TileHolder   _holder;

    private readonly bool _alwaysOn;

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

    /// <param name="alwaysOn">Override for the AlwaysOn gate; null = use Tuning.TreasureAlwaysOn.
    /// Tests pass true directly so the ring-gate branch does not idle the module in prod builds.</param>
    public TreasureMaster(TreasureDb db, IGameMemory? mem = null, bool? alwaysOn = null)
    {
        _db       = db;
        _mem      = mem ?? new LiveMemory();
        _audit    = new ArmAudit(_mem);
        _holder   = new TileHolder(_mem);
        _alwaysOn = alwaysOn ?? Tuning.TreasureAlwaysOn;
    }

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
        // _globalIdle and _globalIdleChecked persist -- the L0 check is startup-once.
    }

    // ── entry point ───────────────────────────────────────────────────────────────

    public void Tick(DateTime now, bool inLive)
    {
        // L0: one-time global idle check (dataset empty, AlwaysOn false, or key mismatch).
        // If CheckGlobalIdle defers (PE header not yet readable), _globalIdleChecked resets
        // to false -- return immediately so the phase switch cannot run before L0 resolves.
        if (!_globalIdleChecked) { CheckGlobalIdle(); if (!_globalIdleChecked) return; }
        if (_globalIdle) return;

        if (!inLive)
        {
            _stableTicks = 0;   // stability counter resets when not in live battle
            return;
        }

        switch (_phase)
        {
            case Phase.Disarmed:      TickDisarmed(); break;
            case Phase.Arming:        TickArming();   break;
            case Phase.Armed:         TickArmed();    break;
            case Phase.BattleDisarmed: break;         // inert until ResetBattle
        }
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

        if (!_alwaysOn)
        {
            _globalIdle = true;
            Log.Info("treasure: ring gate pending -- module idle");
            return;
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

        // Has tiles: transition to ARMING.
        _map         = found;
        _armAttempts = 0;
        _phase       = Phase.Arming;
    }

    // ── ARMING tick ───────────────────────────────────────────────────────────────

    private void TickArming()
    {
        var map = _map!;
        if (!_audit.FingerprintMatches(map))
        {
            _armAttempts++;
            if (_armAttempts >= Tuning.TreasureArmAttemptCap && !_capLoggedThisBattle)
            {
                _capLoggedThisBattle = true;
                Log.Info($"treasure: map {map.MapId} fingerprint mismatch -- " +
                         $"disarmed for battle (unknown variant or game patch)");
                _phase = Phase.BattleDisarmed;
            }
            return;
        }

        var (verdict, _) = _audit.AuditAddrs(map, Tuning.TreasureMinPlausibleAddrs);

        switch (verdict)
        {
            case ArmVerdict.Arm:
                _phase          = Phase.Armed;
                _revalidateTick = 0;
                Log.Info($"treasure: map {map.MapId} {map.Name} armed -- " +
                         $"{map.Tiles.Count} tile(s)");
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

        // Periodic fingerprint revalidation.
        _revalidateTick++;
        if (_revalidateTick >= Tuning.TreasureRevalidateEveryNTicks)
        {
            _revalidateTick = 0;
            if (!_audit.FingerprintMatches(map))
            {
                _phase = Phase.BattleDisarmed;
                Log.Info($"treasure: map {map.MapId} fingerprint changed mid-battle -- disarmed");
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
}
