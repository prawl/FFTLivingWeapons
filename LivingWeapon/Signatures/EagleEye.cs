using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Eclipsebolt's "Eagle Eye" signature. While a +3 Eclipsebolt is equipped by any roster unit, a
/// Doom that appears during the wielder's own action (main hand id 78 in the acted period, the
/// Larceny/Benediction last-actor lane) has its countdown forced down to 1. Doom from any
/// other source is recorded as a baseline and left alone, after Mortal Coil (id 8) was hastened
/// while a +3 Eclipsebolt merely sat fielded. Attribution is action-level, so a foreign Doom landing
/// during the wielder's acted period would be misattributed (accepted).
///
/// The authoritative unit copy (0x14184xxxx, static-array layout) carries Doom at base+0x49
/// (mask 0x01) and the countdown at base+0x59 (init 3, -1 per victim turn, death at 0), per memory
/// doom-status-bytes. The head counter re-renders from the byte and Undead no-op the expiry. Only
/// enemies are scanned, and only a countdown above the target is written.
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
    // ActiveTarget scans roster main hands, so a benched +3 Eclipsebolt arms the edge; the scoped
    // logger hides it unless deployed.
    private readonly ScopedLogger _slog;
    // Per-enemy Doom baseline keyed by authoritative-copy address, to detect a rising edge.
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
        if (ActivationEdge.Step(ref _wasActive, target > 0))
        {
            _slog.Info(_wasActive
                ? "Eclipsebolt at tier three is wielded on the field; the bow's own Doom procs are hastened to one"
                : "Eclipsebolt's Doom hastening is no longer active");
        }
        if (target > 0 && _tick++ % 6 == 0) Hasten(target);   // band scan is heavy -> ~every 200ms
    }

    /// <summary>The Doom countdown target if a +3 Eclipsebolt is equipped by any roster unit, else 0.
    /// Internal so the ghost-row pin test (LW-149 stage D) can call it directly.</summary>
    internal int ActiveTarget()
    {
        if (!_meta.TryGetValue(EclipseboltId, out var m)) return 0;
        int target = AuraTarget(m.Signature, Tuning.TierOf(_kills, EclipseboltId));
        if (target <= 0) return 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            // LW-149 stage D: shared lenient occupied-slot walk (Wielder.Occupancy.cs, also used by
            // Plague.IsEquipped), no level gate.
            if (!Wielder.TryOccupiedSlotLenient(_mem, r, out long rb)) continue;
            // Main hand only.
            if (_mem.U16(rb + Offsets.RRHand) == EclipseboltId)
                return target;
        }
        return 0;
    }

    /// <summary>One BandSweep pass. A Doom rising edge attributed to the wielder's own action (the
    /// Larceny.cs actingMain + actedByte==1 gate) with a countdown above the target is written
    /// down to it. Attribution is computed once per sweep.</summary>
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
                // Fires once per foreign Doom; the baseline keeps it quiet afterwards.
                ModLogger.Debug(LogVerb.Signature, $"a level {fp.lvl} enemy's Doom appeared outside the Eclipsebolt wielder's own action (lastActed mainHand id={_tracker.LastPlayerMainHand}); its countdown ({cd}) is left alone");
                return;
            }
            if (!ShouldHasten(doomed, was, cd, target, attributed)) return;
            long cdAddr = addr + DoomCountdownOff;
            // Re-read under the write guard since the buffered cd can be stale; only write down.
            if (_mem.Writable(cdAddr, 1) && _mem.U8(cdAddr) > target)
            {
                _mem.W8(cdAddr, (byte)target);
                ModLogger.Event(LogVerb.Signature, $"The level {fp.lvl} enemy's Doom countdown is forced to {target} (was {cd}, {fp.mhp} maximum HP); {++_hastened} hastened this battle");
            }
        });
    }

}
