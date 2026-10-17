using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Credits a delayed action (Dragoon Jump, summon or dance charge) to the unit that committed it, since an intervening
/// unit's acted-period overwrites the actor latch before the action lands. Tracks the per-slot delayed-action bit
/// (ADelayedActionMask 0x0C = Jump 0x04 | Charging 0x08), both observed 2026-06-26 (tools/probes/actor_attrib_probe.py,
/// charging_probe.py, status_probe.py; docs/LIVE_LEDGER.md). Tracked path. Snapshot weapons at commit (0->1), arm on landing (1->0).
/// Untracked path (no living weapon): same edges, but a matured kill stamps _lethalUntracked so it earns no credit; a tracked
/// action still wins (delayed != null guard in ScanCorpses). Limits. Consume is global first-credit-wins, concurrent delayed
/// actions share one _delayedActor latch, and UntrackedDelayedWindow (45 ticks, ~1.5s) is untuned; a miss beats a mis-credit.
/// </summary>
internal sealed partial class KillTracker
{
    // delayed-action bit on the previous tick
    private readonly bool[] _performing = new bool[Offsets.BandSlots];
    // per-slot weapon set snapshotted at commit
    private readonly List<int>?[] _chargeWeapons = new List<int>?[Offsets.BandSlots];
    // the most recently armed delayed actor (bit 1->0 transition with a snapshot)
    private List<int>? _delayedActor;
    // ticks left in the arm window; 0 = unarmed
    private int _delayedArmedTicks;
    // per-slot. Committer was an untracked actor (summoner/dancer, no living weapon)
    private readonly bool[] _chargeUntracked = new bool[Offsets.BandSlots];
    // ticks left in the untracked arm window; 0 = unarmed
    private int _untrackedArmedTicks;

    /// <summary>Per-tick tracking. Runs before ScanCorpses so the arm is set before a corpse matures.</summary>
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
                // Invalid slot. Reset without a 1->0 edge so an unmapped read cannot phantom-arm.
                _performing[s] = false;
                _chargeWeapons[s] = null;
                _chargeUntracked[s] = false;
                continue;
            }

            bool delayed = (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADelayedActionMask) != 0;
            byte lvl = _mem.U8(addr + Offsets.ALevel);
            byte br = _mem.U8(addr + Offsets.ABrave);
            byte fa = _mem.U8(addr + Offsets.AFaith);

            // Snapshot at commit. Bit set, latch exists, and this seat is the latched actor, not a bystander.
            // LW-252 stage 3. CommitterMatches prefers nameId equality once both sides have one.
            if (delayed && _chargeWeapons[s] == null
                && _lastPlayerWeapons.Count > 0
                && CommitterMatches(addr, lvl, br, fa))
                _chargeWeapons[s] = new List<int>(_lastPlayerWeapons);
            // Untracked snapshot. Committer is the latched player with no living weapon. _latchResolvedEmpty
            // rejects the never-resolved first-kill state; same CommitterMatches check.
            else if (delayed && !_chargeUntracked[s]
                     && _lastPlayerWeapons.Count == 0 && _latchResolvedEmpty
                     && CommitterMatches(addr, lvl, br, fa))
                _chargeUntracked[s] = true;

            // Arm on 1->0 (action lands) when a weapon snapshot exists.
            if (_performing[s] && !delayed && _chargeWeapons[s] != null)
            {
                _delayedActor = _chargeWeapons[s];
                _delayedArmedTicks = Tuning.DelayedActorWindow;
                _chargeWeapons[s] = null;
                _chargeUntracked[s] = false;   // tracked arm wins this slot; drop any stale untracked marker
            }
            // Untracked arm on 1->0. Open the no-credit window.
            else if (_performing[s] && !delayed && _chargeUntracked[s])
            {
                _untrackedArmedTicks = Tuning.UntrackedDelayedWindow;
                _chargeUntracked[s] = false;
            }

            _performing[s] = delayed;
        }
    }

    /// <summary>LW-252 stage 3. Does band seat <paramref name="addr"/> identify the latched actor? Compares nameId
    /// (Offsets.ANameId) when both <see cref="_lastActorNameId"/> and the seat's own are &gt; 0, which separates fp-twin committers
    /// (ledger [frame-1fc-nameid-mirror], [party-nameid-unique-key]). Otherwise falls back to the (level,brave,faith) compare.
    /// lvl/br/fa are the bytes TrackDelayed already read from <paramref name="addr"/>.</summary>
    private bool CommitterMatches(long addr, byte lvl, byte br, byte fa)
    {
        int seatNameId = _mem.U16(addr + Offsets.ANameId);
        if (_lastActorNameId > 0 && seatNameId > 0) return seatNameId == _lastActorNameId;
        return _lastActorFp == ((int)lvl, (int)br, (int)fa);
    }

    /// <summary>Returns and consumes the armed delayed actor on the first credit in the window; null when unarmed,
    /// so the ?? chain in ScanCorpses falls through to the normal path.</summary>
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
        Array.Clear(_performing, 0, _performing.Length);
        Array.Clear(_chargeWeapons, 0, _chargeWeapons.Length);
        Array.Clear(_chargeUntracked, 0, _chargeUntracked.Length);
        _delayedActor = null;
        _delayedArmedTicks = 0;
        _untrackedArmedTicks = 0;
    }
}
