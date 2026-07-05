using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's signature: while a +3 Galewind is equipped, ONE charm the party lands on an enemy is
/// held UNBREAKABLE for N of that enemy's turns, then force-cleared. Mechanism PROVEN live (memory
/// charm-unbreakable-bytes): on the AUTHORITATIVE unit copy (0x14184xxxx, static-array layout) charm
/// is TWO separate pieces of state, and the breaking hit clears BOTH --
///   * base+0x49 bit 0x20 = charm STATUS / ICON (a memory probe can see this one).
///   * base+0x54 bit 0x20 = charm AI CONTROL / ALLEGIANCE -- who the unit fights for. A probe CANNOT
///     see this (it's AI behaviour, not a visible flag); the live trap was reading the icon persist
///     and assuming the control bit was irrelevant. It is multi-purpose: its low bits drift like an
///     engine counter, so the hold ORs ONLY the 0x20 bit and leaves the counter alone.
/// So the hold re-stamps BOTH bytes each tick to beat the engine's on-hit clear; zeroing BOTH after
/// N turns ends it cleanly (drop only +0x49 and the AI reverts to hostile even with the icon gone).
///
/// ANTI-CHEESE: only ONE enemy is ever locked (else you charm-lock the whole field and win). The
/// lock is newest-wins -- landing a charm on a new enemy drops the previous one and moves the lock
/// (see <see cref="Decide"/>). Other charmed enemies are left as ordinary, breakable charms.
///
/// DETECTION: enumerate REAL enemies from the static array (sane filter -> no phantoms), then in one
/// band pass match each one's fingerprint and read its charm bit LIVE off the authoritative copy.
/// TURN COUNT: off the locked enemy's own CT (+0x25) -- full during its turn, resets when it acts.
/// LIVENESS: no heartbeat. The engine gates Tick on BattleState.BattleDisplayed (mode != 0): on the
/// world map / party menu (mode 0) Tick idles, so the lock goes quiet post-battle. Critically, the
/// between-turn mode-0 LULLS also idle Tick -- it does NOT time the lock out, so the lock survives
/// the lulls and resumes holding when the map redraws. The lock drops itself when the locked enemy's
/// copy stops matching its fingerprint (<see cref="Valid"/>), and is torn down on the debounced
/// battle-exit edge via <see cref="ResetBattle"/>. Every read/write is guarded.
/// </summary>
internal sealed partial class CharmLock : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick(ctx.Now, ctx.InLive);

    internal const int CharmStatusOff = 0x49, CharmAllegOff = 0x54;   // base-relative (internal: tests assert the held bytes)
    private const int CtOff = 0x25;
    internal const byte CharmBit = 0x20;
    private const int GalewindId = 9;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // GATE-ON-ARMED (log facelift): the active/inactive edge fires off a ROSTER scan, so a benched
    // +3 Galewind arms it; the scoped logger demotes those console lines to Debug unless a
    // deployed unit actually wields it this battle.
    private readonly ScopedLogger _slog;
    // The single charm-locked enemy (anti-cheese): authoritative-copy address, its fingerprint,
    // turns counted, and the last CT seen. null = nothing locked.
    private (long addr, (int mhp, int lvl, int br, int fa) fp, int counted, int lastCt)? _lock;
    private bool _wasActive;
    private int _tick, _dbg;

    public CharmLock(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, GalewindId));
    }

    public void ResetBattle() { _lock = null; _wasActive = false; _tick = 0; _dbg = 0; }

    /// <summary>The fingerprint of the one locked enemy, or null (exposed for tests).</summary>
    internal (int mhp, int lvl, int br, int fa)? LockedFingerprint => _lock?.fp;

    /// <param name="inLive">The engine passes <see cref="BattleState.BattleDisplayed"/> here (true on
    /// battle-map frames, mode != 0). False on the world map AND during the between-turn mode-0 lulls:
    /// Tick then no-ops, PRESERVING the lock (there is no heartbeat to time it out), so the lock
    /// survives the lulls and resumes holding when the map redraws. <see cref="ResetBattle"/> tears
    /// the lock down on the real battle-exit edge.</param>
    public void Tick(DateTime now, bool inLive)
    {
        if (!inLive) return;   // world map or a between-turn mode-0 lull: idle, PRESERVING the lock
        int lockTurns = ActiveLockTurns();
        if ((lockTurns > 0) != _wasActive)
        {
            _wasActive = lockTurns > 0;
            _slog.Info(_wasActive
                ? "Galewind at tier three is wielded on the field; charms the party lands are held unbreakable"
                : "Galewind's charm lock is no longer active");
        }
        if (lockTurns > 0 && _tick++ % 6 == 0) Detect();   // band scan is heavy -> ~every 200ms
        Drive(lockTurns);                                   // hold/clear every tick (beats on-hit clear)
    }

    /// <summary>charmLockTurns if a +3 Galewind is equipped by any roster unit, else 0.</summary>
    private int ActiveLockTurns()
    {
        if (!_meta.TryGetValue(GalewindId, out var m) || m.Signature is null || m.Signature.CharmLockTurns <= 0)
            return 0;
        if (Tuning.TierOf(_kills, GalewindId) < m.Signature.AtTier)
            return 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
            // Signatures fire from the main hand only: an offhand Galewind does not lock charms.
            if (_mem.U16(rb + Offsets.RRHand) == GalewindId)
                return m.Signature.CharmLockTurns;
        }
        return 0;
    }

    private void Detect()
    {
        var charmed = ScanCharmed();
        if (charmed.Count > 0) AdoptOrTransfer(charmed);
    }

    /// <summary>Find every real enemy whose authoritative copy shows Charm live. One band pass
    /// (the shared BandSweep), matching each enemy's exact fingerprint -> specific, no noise,
    /// no mirror lag.</summary>
    private List<(long addr, (int mhp, int lvl, int br, int fa) fp)> ScanCharmed()
    {
        var found = new List<(long addr, (int mhp, int lvl, int br, int fa) fp)>();
        BandSweep.ForEachFingerprintHit(_mem, Band.EnemyFingerprints(_mem), (addr, buf, b, fp) =>
        {
            if ((buf[b + CharmStatusOff] & CharmBit) == 0) return;   // this copy isn't charmed
            if (!found.Exists(x => x.fp.Equals(fp))) found.Add((addr, fp));
        });
        return found;
    }

    /// <summary>Newest-wins single lock: adopt the new charm, dropping the previous one if any.</summary>
    internal void AdoptOrTransfer(IReadOnlyList<(long addr, (int mhp, int lvl, int br, int fa) fp)> charmed)
    {
        var fps = new List<(int mhp, int lvl, int br, int fa)>(charmed.Count);
        foreach (var c in charmed) fps.Add(c.fp);
        if (!Decide(_lock?.fp, fps, out var target, out bool dropPrevious)) return;
        if (dropPrevious && _lock is { } prev && Valid(prev.addr, prev.fp))
            SetCharm(prev.addr, false);   // drop the charm from the previous monster
        long addr = 0;
        foreach (var c in charmed) if (c.fp.Equals(target)) { addr = c.addr; break; }
        _lock = (addr, target, 0, _mem.U8(addr + CtOff));
        ModLogger.EventWithTrace(LogVerb.Signature,
            $"Charm is held on the level {target.lvl} enemy ({target.mhp} maximum HP) so it cannot break early{(dropPrevious ? "; the previous lock was dropped" : "")}",
            $"charm lock adopt (struct 0x{addr:X})");
    }

    /// <summary>Hold the charm bytes on the locked enemy, counting its turns off CT; clear after N.</summary>
    // internal for test reach: the hold path is unreachable via Tick (needs a live Galewind roster).
    internal void Drive(int lockTurns)
    {
        if (_lock is not { } L) return;
        if (!Valid(L.addr, L.fp)) { _lock = null; return; }   // copy moved/freed -> re-detect later
        int ct = _mem.U8(L.addr + CtOff);
        int counted = L.counted + (CtTurns.IsTurn(L.lastCt, ct) ? 1 : 0);
        if (_dbg++ % 20 == 0) ModLogger.Debug(LogVerb.Signature, $"charm lock holding: enemy ({L.fp.mhp} maximum HP), {counted}/{lockTurns} of its turns complete (charge time {ct})");
        bool hold = lockTurns > 0 && counted < lockTurns;
        SetCharm(L.addr, hold);
        if (hold) _lock = (L.addr, L.fp, counted, ct);
        else { _lock = null; ModLogger.Event(LogVerb.Signature, $"Charm expired on the enemy ({L.fp.mhp} maximum HP) after {counted} of its turns; the lock is released"); }
    }

    private bool Valid(long b, (int mhp, int lvl, int br, int fa) fp)
    {
        if (!_mem.Readable(b + Offsets.AMaxHp, 2)) return false;
        return _mem.U16(b + Offsets.AMaxHp) == fp.mhp && _mem.U8(b + Offsets.ALevel) == fp.lvl
            && _mem.U8(b + Offsets.ABrave) == fp.br && _mem.U8(b + Offsets.AFaith) == fp.fa;
    }

    // Charm is two pieces of state and the on-hit clear wipes BOTH: +0x49 = status/icon (probe-visible),
    // +0x54 = AI control/allegiance (probe-INvisible). Hold/clear both. Force ORs/ANDs only the 0x20 bit,
    // leaving +0x54's drifting low counter bits untouched.
    private void SetCharm(long b, bool on) { Force(b + CharmStatusOff, on); Force(b + CharmAllegOff, on); }

    private void Force(long addr, bool on)
    {
        if (!_mem.Writable(addr, 1)) return;
        int cur = _mem.U8(addr);
        int want = on ? (cur | CharmBit) : (cur & ~CharmBit);
        if (cur != want) _mem.W8(addr, (byte)want);
    }
}
