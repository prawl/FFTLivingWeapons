using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's signature: while a +3 Galewind is equipped, ONE charm the party lands on an enemy is
/// held UNBREAKABLE for N of that enemy's turns, then force-cleared. Mechanism PROVEN live (memory
/// charm-unbreakable-bytes): the AUTHORITATIVE unit copy (0x14184xxxx, static-array layout) carries
/// charm at base+0x49 (status, mask 0x20) and an allegiance flag at base+0x54 (0x20). Re-stamping
/// both each tick beats the engine's on-hit clear; zeroing them after N turns ends it cleanly.
///
/// ANTI-CHEESE: only ONE enemy is ever locked (else you charm-lock the whole field and win). The
/// lock is newest-wins -- landing a charm on a new enemy drops the previous one and moves the lock
/// (see <see cref="Decide"/>). Other charmed enemies are left as ordinary, breakable charms.
///
/// DETECTION: enumerate REAL enemies from the static array (sane filter -> no phantoms), then in one
/// band pass match each one's fingerprint and read its charm bit LIVE off the authoritative copy.
/// TURN COUNT: off the locked enemy's own CT (+0x25) -- full during its turn, resets when it acts.
/// HEARTBEAT: the engine pings on every live-battlefield tick; if pings lapse (battle ended even
/// though the sticky sentinel lies) the lock deactivates and goes quiet. Every read/write is guarded.
/// </summary>
internal sealed partial class CharmLock
{
    internal const int CharmStatusOff = 0x49, CharmAllegOff = 0x54;   // base-relative (internal: tests assert the held bytes)
    private const int CtOff = 0x25;
    internal const byte CharmBit = 0x20;
    private const int GalewindId = 9;
    private const long BandRadius = 0x100000;   // +/-1MB around the combat anchor

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // The single charm-locked enemy (anti-cheese): authoritative-copy address, its fingerprint,
    // turns counted, and the last CT seen. null = nothing locked.
    private (long addr, (int mhp, int lvl, int br, int fa) fp, int counted, int lastCt)? _lock;
    private DateTime _lastBeat;
    private bool _wasActive;
    private int _tick, _dbg;

    public CharmLock(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle() { _lock = null; _wasActive = false; _tick = 0; _dbg = 0; _lastBeat = default; }

    /// <summary>The fingerprint of the one locked enemy, or null (exposed for tests).</summary>
    internal (int mhp, int lvl, int br, int fa)? LockedFingerprint => _lock?.fp;

    /// <summary>Engine pings this on every live-battlefield tick; lapsed pings time the lock out.</summary>
    public void Heartbeat(DateTime now) => _lastBeat = now;

    /// <param name="inLive">True on a genuine live-battlefield frame (<see cref="InLiveBattle"/>).
    /// False: only the heartbeat-timeout release path runs; no Detect, Drive, or writes.</param>
    public void Tick(DateTime now, bool inLive)
    {
        if (HeartbeatExpired(now, _lastBeat, TimeoutMs)) { Deactivate(); return; }
        if (!inLive) return;
        int lockTurns = ActiveLockTurns();
        if ((lockTurns > 0) != _wasActive)
        {
            _wasActive = lockTurns > 0;
            Log.Info($"charm-lock {(_wasActive ? "ACTIVE -- Galewind at +3 is equipped, charm holds are now unbreakable" : "inactive")}");
        }
        if (lockTurns > 0 && _tick++ % 6 == 0) Detect();   // band scan is heavy -> ~every 200ms
        Drive(lockTurns);                                   // hold/clear every tick (beats on-hit clear)
    }

    /// <summary>Heartbeat lapsed (battle ended). Drop the lock and go quiet; log the edge once.</summary>
    private void Deactivate()
    {
        _lock = null;
        _tick = 0;
        _dbg = 0;
        if (_wasActive) { _wasActive = false; Log.Info("charm-lock: inactive -- battle ended, lock released"); }
    }

    /// <summary>charmLockTurns if a +3 Galewind is equipped by any roster unit, else 0.</summary>
    private int ActiveLockTurns()
    {
        if (!_meta.TryGetValue(GalewindId, out var m) || m.Signature is null || m.Signature.CharmLockTurns <= 0)
            return 0;
        if (Tuning.TierFor(_kills.TryGetValue(GalewindId, out int k) ? k : 0) < m.Signature.AtTier)
            return 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!Mem.Readable(rb + Offsets.RNameId, 2)) continue;
            // Signatures fire from the main hand only: an offhand Galewind does not lock charms.
            if (Mem.U16(rb + Offsets.RRHand) == GalewindId)
                return m.Signature.CharmLockTurns;
        }
        return 0;
    }

    private void Detect()
    {
        var charmed = ScanCharmed();
        if (charmed.Count > 0) AdoptOrTransfer(charmed);
    }

    /// <summary>Find every real enemy whose authoritative copy shows Charm live. One band pass,
    /// matching each enemy's exact fingerprint -> specific, no noise, no mirror lag.</summary>
    private List<(long addr, (int mhp, int lvl, int br, int fa) fp)> ScanCharmed()
    {
        var found = new List<(long addr, (int mhp, int lvl, int br, int fa) fp)>();
        var byMhp = new Dictionary<int, List<(int mhp, int lvl, int br, int fa)>>();
        foreach (var fp in Enemies())
        {
            if (!byMhp.TryGetValue(fp.mhp, out var l)) byMhp[fp.mhp] = l = new();
            l.Add(fp);
        }
        if (byMhp.Count == 0) return found;
        long lo = Offsets.CombatAnchor - BandRadius, total = BandRadius * 2;
        const int chunk = 0x40000;
        for (long off = 0; off < total; off += chunk)
        {
            int n = (int)Math.Min(chunk + 0x80, total - off);
            if (!Mem.TryReadBytes(lo + off, n, out byte[] buf)) continue;   // RPM reads across regions
            int lim = Math.Min(chunk, buf.Length - 0x80);
            for (int i = Offsets.AMaxHp; i < lim; i++)
            {
                int mhp = buf[i] | (buf[i + 1] << 8);
                if (!byMhp.TryGetValue(mhp, out var cands)) continue;
                int b = i - Offsets.AMaxHp;
                foreach (var fp in cands)
                {
                    if (buf[b + Offsets.ALevel] != fp.lvl || buf[b + Offsets.ABrave] != fp.br || buf[b + Offsets.AFaith] != fp.fa) continue;
                    if ((buf[b + CharmStatusOff] & CharmBit) == 0) continue;   // this copy isn't charmed
                    if (!found.Exists(x => x.fp.Equals(fp))) found.Add((lo + off + b, fp));
                    break;
                }
            }
        }
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
        _lock = (addr, target, 0, Mem.U8(addr + CtOff));
        Log.Info($"charm-lock: holding Charm on the level-{target.lvl} enemy ({target.mhp} max HP) so it cannot break early (struct at 0x{addr:X}){(dropPrevious ? " -- dropped previous lock" : "")}");
    }

    /// <summary>Real enemy fingerprints from the static array; filters phantoms (level 0 / garbage).</summary>
    private List<(int mhp, int lvl, int br, int fa)> Enemies()
    {
        var list = new List<(int, int, int, int)>();
        for (int s = 0; s <= Offsets.EnemySlotMax; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            if (!Mem.Readable(slot + Offsets.AMaxHp, 2)) continue;
            int mhp = Mem.U16(slot + Offsets.AMaxHp), lvl = Mem.U8(slot + Offsets.ALevel);
            int br = Mem.U8(slot + Offsets.ABrave), fa = Mem.U8(slot + Offsets.AFaith);
            if (mhp < 1 || mhp > 2000 || lvl < 1 || lvl > 99 || br > 100 || fa > 100) continue;
            var fp = (mhp, lvl, br, fa);
            if (!list.Contains(fp)) list.Add(fp);
        }
        return list;
    }

    /// <summary>Hold the charm bytes on the locked enemy, counting its turns off CT; clear after N.</summary>
    private void Drive(int lockTurns)
    {
        if (_lock is not { } L) return;
        if (!Valid(L.addr, L.fp)) { _lock = null; return; }   // copy moved/freed -> re-detect later
        int ct = Mem.U8(L.addr + CtOff);
        int counted = L.counted + (IsTurn(L.lastCt, ct) ? 1 : 0);
        if (_dbg++ % 20 == 0) Log.Info($"charm-lock: locked enemy ({L.fp.mhp} max HP) -- holding for {counted}/{lockTurns} of its turns (CT {ct})");
        bool hold = lockTurns > 0 && counted < lockTurns;
        SetCharm(L.addr, hold);
        if (hold) _lock = (L.addr, L.fp, counted, ct);
        else { _lock = null; Log.Info($"charm-lock: Charm expired on the enemy ({L.fp.mhp} max HP) after {counted} turns -- lock released"); }
    }

    private static bool Valid(long b, (int mhp, int lvl, int br, int fa) fp)
    {
        if (!Mem.Readable(b + Offsets.AMaxHp, 2)) return false;
        return Mem.U16(b + Offsets.AMaxHp) == fp.mhp && Mem.U8(b + Offsets.ALevel) == fp.lvl
            && Mem.U8(b + Offsets.ABrave) == fp.br && Mem.U8(b + Offsets.AFaith) == fp.fa;
    }

    private static void SetCharm(long b, bool on) { Force(b + CharmStatusOff, on); Force(b + CharmAllegOff, on); }

    private static void Force(long addr, bool on)
    {
        if (!Mem.Writable(addr, 1)) return;
        int cur = Mem.U8(addr);
        int want = on ? (cur | CharmBit) : (cur & ~CharmBit);
        if (cur != want) Mem.W8(addr, (byte)want);
    }
}
