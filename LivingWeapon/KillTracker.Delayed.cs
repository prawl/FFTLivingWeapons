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
/// Charging 0x08: PROVEN LIVE 2026-06-26 -- SET observed (charging_probe.py); the untracked-arm
///   cross-turn summon no-credit fires in-game (which requires the 1->0 landing edge). See docs/LIVE_LEDGER.md.
/// Source: tools/probes/status_probe.py decode map + watchweapon trace.
///
/// TRACKED path (Jump / tracked charge): per-slot snapshot at commit (bit 0->1, weapons non-empty);
///   arm on landing (1->0); ConsumeDelayedCulprit() credits the committed weapon over any stale latch.
/// UNTRACKED path (summoner / dancer charged action, no living weapon): per-slot snapshot at commit
///   (bit 0->1, latch resolved empty); arm on landing (1->0, _untrackedArmedTicks); matured kills
///   stamp _lethalUntracked -> no credit -- the cross-turn kill is buried instead of leaking to the
///   next armed unit that latches. A tracked delayed action still wins (delayed != null guard in
///   ScanCorpses), so Jump and charged-summon can coexist without suppressing each other.
///
/// V1 limitations (documented, accepted):
///   -- consume is global first-credit-wins: an unrelated corpse maturing the same tick at a
///      lower slot index can steal the delayed actor (slot-order nondeterminism). Mitigated by
///      the tight DelayedActorWindow and Jump being single-target.
///   -- concurrent delayed actions share one _delayedActor latch; most-recent clear wins.
///      _chargeWeapons[] is per-slot so snapshots do not collide.
///   -- the UntrackedDelayedWindow (45 ticks ~1.5s) is an untuned hedge; an unrelated armed kill
///      maturing inside it degrades to a no-credit miss. Miss beats mis-credit.
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
    // per-slot: the delayed bit was set while THIS slot was the latched UNTRACKED (no living weapon)
    // actor -- a summoner/dancer charged-action commit. Parallel to _chargeWeapons (which only ever
    // holds NON-empty weapon sets).
    private readonly bool[] _chargeUntracked = new bool[Offsets.BandSlots];
    // ticks remaining in the UNTRACKED arm window; 0 = unarmed. Global (mirror _delayedArmedTicks).
    private int _untrackedArmedTicks;

    /// <summary>Per-tick delayed-action tracking. Must run BEFORE ScanCorpses so the arm is
    /// set before a corpse matures on the same tick.</summary>
    private void TrackDelayed(bool onField)
    {
        if (!onField) return;
        if (_delayedArmedTicks > 0) _delayedArmedTicks--;
        if (_untrackedArmedTicks > 0) _untrackedArmedTicks--;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr))
            {
                // Invalid slot: clear state without treating it as a 1->0 transition (a
                // fail-safe 0 read from an unmapped addr must not phantom-arm).
                _performing[s] = false;
                _chargeWeapons[s] = null;
                _chargeUntracked[s] = false;
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
            // UNTRACKED snapshot: the committer is the latched roster player with NO living weapon
            // (summoner/dancer). Mutually exclusive with the tracked snapshot within a tick (opposite
            // _lastPlayerWeapons.Count). _latchResolvedEmpty rejects the "never-resolved" first-kill
            // state (Count==0 but no resolved player); the fp-match targets THIS slot, not a bystander.
            else if (delayed && !_chargeUntracked[s]
                     && _lastPlayerWeapons.Count == 0 && _latchResolvedEmpty
                     && _lastActorFp == ((int)lvl, (int)br, (int)fa))
                _chargeUntracked[s] = true;

            // ARM on 1->0 (action lands): tracked arm fires when we have a weapon snapshot.
            if (_performing[s] && !delayed && _chargeWeapons[s] != null)
            {
                _delayedActor        = _chargeWeapons[s];
                _delayedArmedTicks   = Tuning.DelayedActorWindow;
                _chargeWeapons[s]    = null;
                _chargeUntracked[s]  = false;   // tracked arm wins this slot; drop any stale untracked marker
            }
            // UNTRACKED arm on 1->0: a cross-turn untracked charge lands -> open the no-credit window.
            else if (_performing[s] && !delayed && _chargeUntracked[s])
            {
                _untrackedArmedTicks = Tuning.UntrackedDelayedWindow;
                _chargeUntracked[s]  = false;
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
        Array.Clear(_performing,      0, _performing.Length);
        Array.Clear(_chargeWeapons,   0, _chargeWeapons.Length);
        Array.Clear(_chargeUntracked, 0, _chargeUntracked.Length);
        _delayedActor        = null;
        _delayedArmedTicks   = 0;
        _untrackedArmedTicks = 0;
    }
}
