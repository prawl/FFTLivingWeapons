using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Eclipsebolt's "Eagle Eye" signature: while a +3 Eclipsebolt is equipped by any roster unit,
/// every Doom on an ENEMY is hastened -- its countdown is forced down to 1, so the mark resolves
/// on the victim's next turn instead of three turns out. PROVEN live (memory doom-status-bytes):
/// the authoritative unit copy (0x14184xxxx, static-array layout) carries Doom at base+0x49 (mask
/// 0x01) and the countdown at base+0x59 (init 3, -1 per the victim's turn, death at 0); the
/// head-counter re-renders LIVE from the byte -- no cosmetic seam (unlike poison's tint). Undead
/// no-op Doom's expiry, so the engine simply declines the kill -- no special-casing here.
///
/// Idempotent: only an enemy whose countdown is STILL above the target (1) is written, so once
/// hastened we never touch it again -- the engine ticks it 1 -> 0 -> death untouched. Only ENEMIES
/// are scanned (a Doomed ally is never sped up). Mirrors CharmLock's enemy-scan + auth-copy read.
/// Every read/write is VirtualQuery-guarded.
/// </summary>
internal sealed partial class EagleEye
{
    internal const int DoomStatusOff = 0x49, DoomCountdownOff = 0x59;   // base-relative (tests assert via Policy)
    internal const byte DoomBit = 0x01;                                  // shares +0x49 with Charm (0x20), different bit
    private const int EclipseboltId = 78;
    private const long BandRadius = 0x100000;   // +/-1MB around the combat anchor

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private bool _wasActive;
    private int _tick, _hastened;

    public EagleEye(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills)
    {
        _meta = meta;
        _kills = kills;
    }

    public void ResetBattle() { _wasActive = false; _tick = 0; _hastened = 0; }

    public void Tick()
    {
        int target = ActiveTarget();
        if ((target > 0) != _wasActive)
        {
            _wasActive = target > 0;
            Log.Info($"eagle-eye {(_wasActive ? "ACTIVE -- Eclipsebolt at +3 is equipped, enemy Doom countdowns are forced to 1" : "inactive")}");
        }
        if (target > 0 && _tick++ % 6 == 0) Hasten(target);   // band scan is heavy -> ~every 200ms
    }

    /// <summary>DoomCountdownTo if a +3 Eclipsebolt is equipped by any roster unit, else 0.
    /// Mirrors CharmLock.ActiveLockTurns (signature gate, then a roster sweep for the weapon).</summary>
    private int ActiveTarget()
    {
        if (!_meta.TryGetValue(EclipseboltId, out var m)) return 0;
        int target = AuraTarget(m.Signature, Tuning.TierFor(_kills.TryGetValue(EclipseboltId, out int k) ? k : 0));
        if (target <= 0) return 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!Mem.Readable(rb + Offsets.RNameId, 2)) continue;
            // Signatures fire from the main hand only: an offhand Eclipsebolt does not hasten Doom.
            if (Mem.U16(rb + Offsets.RRHand) == EclipseboltId)
                return target;
        }
        return 0;
    }

    /// <summary>One band pass: every real enemy showing Doom live on its authoritative copy, whose
    /// countdown is still above the target, gets that countdown written down to the target.</summary>
    private void Hasten(int target)
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
                    bool doomed = (buf[b + DoomStatusOff] & DoomBit) != 0;
                    int cd = buf[b + DoomCountdownOff];
                    if (ShouldHasten(doomed, cd, target))
                    {
                        long addr = lo + off + b + DoomCountdownOff;
                        // re-read under the write guard (the buffered cd can be stale by now); only ever WRITE DOWN.
                        if (Mem.Writable(addr, 1) && Mem.U8(addr) > target)
                        {
                            Mem.W8(addr, (byte)target);
                            Log.Info($"eagle-eye: enemy Doom countdown forced to {target} (was {cd}) -- level-{fp.lvl} enemy ({fp.mhp} max HP) [{++_hastened} this battle]");
                        }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>Real ENEMY fingerprints from the static array; filters phantoms (level 0 / garbage).</summary>
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
}
