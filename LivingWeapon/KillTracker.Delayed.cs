using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Delayed-action kill attribution. Dragoon Jump commits on the jumper's turn but lands a
/// full turn later -- an intervening unit's acted-period overwrites the actor latch, so the
/// stale latch credits the wrong weapon. This partial tracks the per-slot delayed-action bit
/// (ADelayedActionMask 0x0C = Jump 0x04 | Charging 0x08) and arms the correct committer.
///
/// Jump 0x04: PROVEN LIVE 2026-06-26 (tools/probes/actor_attrib_probe.py watchweapon trace;
///   status[45] 00->04 at jump-commit, 04->00 at landing ~8.6s later; live: Reis latch
///   Hexweave Bag (118) committed, Ramza Chaos Blade (37) overwrote, kill credited to 37).
/// Charging 0x08: same mechanism (charged spells / Charge skill); live-UNVERIFIED 2026-06-26.
/// Source: tools/probes/status_probe.py decode map + watchweapon trace.
///
/// V1 limitations (documented, accepted):
///   -- consume is global first-credit-wins: an unrelated corpse maturing the same tick at a
///      lower slot index can steal the delayed actor (slot-order nondeterminism). Mitigated by
///      the tight DelayedActorWindow and Jump being single-target.
///   -- concurrent delayed actions share one _delayedActor latch; most-recent clear wins.
///      _chargeWeapons[] is per-slot so snapshots do not collide.
/// </summary>
internal sealed partial class KillTracker
{
    // per-slot: whether the delayed-action bit was set on the PREVIOUS tick
    private readonly bool[] _performing = new bool[Offsets.BandSlots];
    // per-slot: weapon set snapshotted at commit (bit 0->1 while this slot IS the latched actor)
    private readonly List<int>?[] _chargeWeapons = new List<int>?[Offsets.BandSlots];
    // the most recently armed delayed actor (bit 1->0 transition with a snapshot)
    private List<int>? _delayedActor;
    // ticks remaining in the arm window; 0 = unarmed
    private int _delayedArmedTicks;

    /// <summary>Per-tick delayed-action tracking. Must run BEFORE ScanCorpses so the arm is
    /// set before a corpse matures on the same tick.</summary>
    private void TrackDelayed(bool onField)
    {
        if (!onField) return;
        if (_delayedArmedTicks > 0) _delayedArmedTicks--;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr))
            {
                // Invalid slot: clear state without treating it as a 1->0 transition (a
                // fail-safe 0 read from an unmapped addr must not phantom-arm).
                _performing[s] = false;
                _chargeWeapons[s] = null;
                continue;
            }

            bool delayed = (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADelayedActionMask) != 0;
            byte lvl = _mem.U8(addr + Offsets.ALevel);
            byte br  = _mem.U8(addr + Offsets.ABrave);
            byte fa  = _mem.U8(addr + Offsets.AFaith);

            // SNAPSHOT at commit: bit set, no snapshot yet, a latch exists, AND this slot's
            // fingerprint matches the latched actor (so we capture the committer, not a bystander
            // whose bit happened to be set while a different actor was the latch).
            if (delayed && _chargeWeapons[s] == null
                && _lastPlayerWeapons.Count > 0
                && _lastActorFp == ((int)lvl, (int)br, (int)fa))
                _chargeWeapons[s] = new List<int>(_lastPlayerWeapons);

            // ARM on 1->0 (action lands): fire only when we have a snapshot to arm with.
            if (_performing[s] && !delayed && _chargeWeapons[s] != null)
            {
                _delayedActor        = _chargeWeapons[s];
                _delayedArmedTicks   = Tuning.DelayedActorWindow;
                _chargeWeapons[s]    = null;
            }

            _performing[s] = delayed;
        }
    }

    /// <summary>Return and consume the armed delayed actor on the first credit within the
    /// window. Returns null (never an empty non-null list) when unarmed, so the ?? chain in
    /// ScanCorpses falls through to the normal lethal-stamp / live-latch path.</summary>
    private List<int>? ConsumeDelayedCulprit()
    {
        if (_delayedArmedTicks > 0 && _delayedActor != null && _delayedActor.Count > 0)
        {
            _delayedArmedTicks = 0;
            return _delayedActor;
        }
        return null;
    }

    /// <summary>Reset delayed-action state. Called from ResetBattle.</summary>
    private void ResetDelayed()
    {
        Array.Clear(_performing,    0, _performing.Length);
        Array.Clear(_chargeWeapons, 0, _chargeWeapons.Length);
        _delayedActor      = null;
        _delayedArmedTicks = 0;
    }
}
