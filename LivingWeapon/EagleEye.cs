using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Eclipsebolt's "Eagle Eye" signature: while a +3 Eclipsebolt is equipped by any roster unit,
/// a Doom that APPEARS while the Eclipsebolt wielder's own action is resolving (acting main hand
/// id 78 during the acted period, the Larceny/Benediction last-actor lane) is hastened -- its
/// countdown is forced down to 1, so the mark resolves on the victim's next turn instead of three
/// turns out. LW-95: a Doom from any other source (another weapon's proc, an enemy cast) is
/// observed as a baseline and left alone -- live evidence was Mortal Coil (id 8) proccing Doom
/// while a +3 Eclipsebolt merely sat fielded, and Eagle Eye hastened it anyway. Accepted residual:
/// attribution is action-level, so a Doom landing on an enemy from a non-wielder source during the
/// wielder's own acted period would attribute falsely (charm-tier obscurity, accepted).
///
/// PROVEN live (memory doom-status-bytes): the authoritative unit copy (0x14184xxxx, static-array
/// layout) carries Doom at base+0x49 (mask 0x01) and the countdown at base+0x59 (init 3, -1 per
/// the victim's turn, death at 0); the head-counter re-renders LIVE from the byte -- no cosmetic
/// seam (unlike poison's tint). Undead no-op Doom's expiry, so the engine simply declines the
/// kill -- no special-casing here.
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
    private readonly KillTracker _tracker;
    // GATE-ON-ARMED (log facelift): ActiveTarget scans ROSTER main hands, so a benched +3
    // Eclipsebolt arms the edge; the scoped logger keeps it off the console unless deployed.
    private readonly ScopedLogger _slog;
    // LW-95: per-enemy Doom baseline (keyed by authoritative-copy address) so Hasten can tell a
    // fresh appearance (rising edge) from a Doom that was already sitting on the foe.
    private readonly Dictionary<long, bool> _wasDoomed = new();
    private bool _wasActive;
    private int _tick, _hastened;

    public EagleEye(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, KillTracker tracker, IGameMemory? mem = null)
    {
        _mem = mem ?? new LiveMemory();
        _meta = meta;
        _kills = kills;
        _tracker = tracker;
        _slog = ModLogger.For(LogVerb.Signature, () => Wielder.AnyDeployedMainHand(_mem, EclipseboltId));
    }

    public void ResetBattle() { _wasActive = false; _tick = 0; _hastened = 0; _wasDoomed.Clear(); }

    public void Tick()
    {
        int target = ActiveTarget();
        if ((target > 0) != _wasActive)
        {
            _wasActive = target > 0;
            _slog.Info(_wasActive
                ? "Eclipsebolt at tier three is wielded on the field; the bow's own Doom procs are hastened to one"
                : "Eclipsebolt's Doom hastening is no longer active");
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

    /// <summary>One band pass (the shared BandSweep): only a Doom RISING EDGE attributed to the
    /// Eclipsebolt wielder's own action (mirrors Larceny.cs's actingMain + actedByte==1 gate),
    /// whose countdown is still above the target, gets that countdown written down to the
    /// target. The attribution gate is computed ONCE per sweep, not per enemy.</summary>
    private void Hasten(int target)
    {
        bool attributed = Signatures.IsActingMainHand(_tracker.LastPlayerMainHand, EclipseboltId)
                          && _mem.Readable(Offsets.Acted, 1) && _mem.U8(Offsets.Acted) == 1;

        BandSweep.ForEachFingerprintHit(_mem, Band.EnemyFingerprints(_mem), (addr, buf, b, fp) =>
        {
            bool doomed = (buf[b + DoomStatusOff] & DoomBit) != 0;
            int cd = buf[b + DoomCountdownOff];
            bool was = _wasDoomed.TryGetValue(addr, out var w) && w;
            _wasDoomed[addr] = doomed;

            if (doomed && !was && !attributed)
            {
                // Edge-only, so this cannot flood: it fires once when a foreign Doom appears, then
                // the baseline above holds it quiet for the rest of the mark's life.
                ModLogger.Debug(LogVerb.Signature, $"a level {fp.lvl} enemy's Doom appeared outside the Eclipsebolt wielder's own action (lastActed mainHand id={_tracker.LastPlayerMainHand}); its countdown ({cd}) is left alone");
                return;
            }
            if (!ShouldHasten(doomed, was, cd, target, attributed)) return;
            long cdAddr = addr + DoomCountdownOff;
            // re-read under the write guard (the buffered cd can be stale by now); only ever WRITE DOWN.
            if (_mem.Writable(cdAddr, 1) && _mem.U8(cdAddr) > target)
            {
                _mem.W8(cdAddr, (byte)target);
                ModLogger.Event(LogVerb.Signature, $"The level {fp.lvl} enemy's Doom countdown is forced to {target} (was {cd}, {fp.mhp} maximum HP); {++_hastened} hastened this battle");
            }
        });
    }

}
