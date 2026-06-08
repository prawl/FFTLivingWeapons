using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Galewind's signature: while a +3 Galewind is equipped, any Charm the party lands on an enemy is
/// held UNBREAKABLE for N of that enemy's turns, then force-cleared. Mechanism PROVEN live (memory
/// charm-unbreakable-bytes): the AUTHORITATIVE unit copy (0x14184xxxx, static-array layout) carries
/// charm at base+0x49 (status, mask 0x20) and an allegiance flag at base+0x54 (0x20). Re-stamping
/// both each tick beats the engine's on-hit clear; zeroing them after N turns ends it cleanly.
///
/// DETECTION: enumerate REAL enemies from the static array (sane filter -> no phantoms), then in one
/// band pass match each one's 5-field fingerprint and read its charm bit LIVE off the authoritative
/// copy (the 0x140893C00 mirror lags + a raw bit-scan is noise). TURN COUNT: off the locked enemy's
/// own CT (+0x25) -- it sits full during its turn then resets when it acts; a reset = one turn. (The
/// cross-array active-unit turn-resolver was unreliable live; this self-contained CT edge is robust.)
/// Every read/write is Mem-guarded.
/// </summary>
internal sealed class CharmLock
{
    private const int CharmStatusOff = 0x49, CharmAllegOff = 0x54, CtOff = 0x25;   // base-relative
    private const byte CharmBit = 0x20;
    private const int GalewindId = 9;
    private const long BandRadius = 0x100000;   // +/-1MB around the combat anchor

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    // charm-locked authoritative copies: address -> (enemy fingerprint, turns counted, last CT seen)
    private readonly Dictionary<long, ((int mhp, int lvl, int br, int fa) fp, int counted, int lastCt)> _locks = new();
    private bool _wasActive;
    private int _tick, _dbg;

    public CharmLock(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle() { _locks.Clear(); _wasActive = false; _tick = 0; }

    /// <summary>A completed turn = the unit's CT was (near-)full and has since reset notably lower.</summary>
    public static bool IsTurn(int lastCt, int curCt) => lastCt >= 90 && curCt < 70;

    public void Tick()
    {
        int lockTurns = ActiveLockTurns();
        if ((lockTurns > 0) != _wasActive)
        {
            _wasActive = lockTurns > 0;
            Log.Info($"charm-lock {(_wasActive ? "ACTIVE (+3 Galewind equipped)" : "inactive")}");
        }
        if (lockTurns > 0 && _tick++ % 6 == 0) Detect();   // band scan is heavy -> ~every 200ms
        Drive(lockTurns);                                   // hold/clear every tick (beats on-hit clear)
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
            if (Mem.U16(rb + Offsets.RRHand) == GalewindId || Mem.U16(rb + Offsets.ROffHand) == GalewindId)
                return m.Signature.CharmLockTurns;
        }
        return 0;
    }

    /// <summary>Lock any real enemy whose authoritative copy shows Charm live. One band pass, matching
    /// each enemy's exact fingerprint -> specific, no noise, no mirror lag.</summary>
    private void Detect()
    {
        var byMhp = new Dictionary<int, List<(int mhp, int lvl, int br, int fa)>>();
        foreach (var fp in Enemies())
        {
            if (!byMhp.TryGetValue(fp.mhp, out var l)) byMhp[fp.mhp] = l = new();
            l.Add(fp);
        }
        if (byMhp.Count == 0) return;
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
                    long addr = lo + off + b;
                    if (_locks.ContainsKey(addr)) break;
                    _locks[addr] = (fp, 0, buf[b + CtOff]);
                    Log.Info($"charm-lock armed: mhp {fp.mhp} lvl {fp.lvl} @ {addr:X}");
                    break;
                }
            }
        }
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

    /// <summary>Hold the charm bytes on each locked enemy, counting its turns off CT; clear after N.</summary>
    private void Drive(int lockTurns)
    {
        if (_locks.Count == 0) return;
        foreach (long addr in new List<long>(_locks.Keys))
        {
            var (fp, counted, lastCt) = _locks[addr];
            if (!Valid(addr, fp)) { _locks.Remove(addr); continue; }   // copy moved/freed -> re-detect later
            int ct = Mem.U8(addr + CtOff);
            if (IsTurn(lastCt, ct)) counted++;
            if (_dbg++ % 20 == 0) Log.Info($"charm-lock mhp {fp.mhp}: CT {ct} turns {counted}/{lockTurns}");
            bool hold = lockTurns > 0 && counted < lockTurns;
            SetCharm(addr, hold);
            if (hold) _locks[addr] = (fp, counted, ct);
            else { _locks.Remove(addr); Log.Info($"charm-lock cleared: mhp {fp.mhp} after {counted} turns"); }
        }
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
