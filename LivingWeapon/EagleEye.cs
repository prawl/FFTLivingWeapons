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
internal sealed partial class EagleEye : ISignature
{
    void ISignature.Tick(in TickContext ctx) => Tick();
    internal const int DoomStatusOff = 0x49, DoomCountdownOff = 0x59;   // base-relative (tests assert via Policy)
    internal const byte DoomBit = 0x01;                                  // shares +0x49 with Charm (0x20), different bit
    private const int EclipseboltId = 78;

    private readonly IGameMemory _mem;   // injected (LiveMemory in production; fakes in tests)

    private readonly Dictionary<int, WeaponMeta> _meta;
    private readonly Dictionary<int, int> _kills;
    private bool _wasActive;
    private int _tick, _hastened;

    public EagleEye(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
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
            ModLogger.Log($"eagle-eye {(_wasActive ? "ACTIVE -- Eclipsebolt at +3 is equipped, enemy Doom countdowns are forced to 1" : "inactive")}");
        }
        if (target > 0 && _tick++ % 6 == 0) Hasten(target);   // band scan is heavy -> ~every 200ms
    }

    /// <summary>DoomCountdownTo if a +3 Eclipsebolt is equipped by any roster unit, else 0.
    /// Mirrors CharmLock.ActiveLockTurns (signature gate, then a roster sweep for the weapon).</summary>
    private int ActiveTarget()
    {
        if (!_meta.TryGetValue(EclipseboltId, out var m)) return 0;
        int target = AuraTarget(m.Signature, Tuning.TierOf(_kills, EclipseboltId));
        if (target <= 0) return 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            if (!_mem.Readable(rb + Offsets.RNameId, 2)) continue;
            // Signatures fire from the main hand only: an offhand Eclipsebolt does not hasten Doom.
            if (_mem.U16(rb + Offsets.RRHand) == EclipseboltId)
                return target;
        }
        return 0;
    }

    /// <summary>One band pass (the shared BandSweep): every real enemy showing Doom live on its
    /// authoritative copy, whose countdown is still above the target, gets that countdown
    /// written down to the target.</summary>
    private void Hasten(int target)
    {
        BandSweep.ForEachFingerprintHit(_mem, Band.EnemyFingerprints(_mem), (addr, buf, b, fp) =>
        {
            bool doomed = (buf[b + DoomStatusOff] & DoomBit) != 0;
            int cd = buf[b + DoomCountdownOff];
            if (!ShouldHasten(doomed, cd, target)) return;
            long cdAddr = addr + DoomCountdownOff;
            // re-read under the write guard (the buffered cd can be stale by now); only ever WRITE DOWN.
            if (_mem.Writable(cdAddr, 1) && _mem.U8(cdAddr) > target)
            {
                _mem.W8(cdAddr, (byte)target);
                ModLogger.Log($"eagle-eye: enemy Doom countdown forced to {target} (was {cd}) -- level-{fp.lvl} enemy ({fp.mhp} max HP) [{++_hastened} this battle]");
            }
        });
    }

}
