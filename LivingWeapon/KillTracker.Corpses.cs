using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Band corpse scan and alive-edge belt -- the per-slot death state machine half of
/// KillTracker (the actor latch lives in KillTracker.cs; the enemy-side identity set is
/// <see cref="EnemyOracle"/>).
///
/// DEAD DETECTION: hp==0 OR Dead status bit (+0x45/0x20, proven from Doom research). A
/// status-death (e.g. Phoenix Down on undead) fires the bit without HP ever reaching zero.
///
/// ALIVE-EDGE BELT: replaces the once-ever identity dedup. Tracks per-identity last-seen-alive
/// state (_identityAlive). Credit fires only on alive->dead TRANSITION. The identity becomes
/// creditable again once observed alive (hp>0, Dead bit clear) after a death. Frozen-twin
/// protection survives: a dead identity at two addresses simultaneously credits once, because
/// the twin is never independently observed alive in between.
///
/// Per-slot alive/dead streaks with 3-tick guards defeat phantom-load transients.
/// </summary>
internal sealed partial class KillTracker
{
    private const int AliveNeeded = 3;  // consecutive valid alive ticks before seenAlive is set
    private const int DeadNeeded = 3;   // consecutive dead ticks before a credit attempt

    // per-slot state (band slot index = 0..BandSlots-1)
    private readonly bool[] _seenAlive = new bool[Offsets.BandSlots];
    private readonly bool[] _deadCredited = new bool[Offsets.BandSlots];
    private readonly int[] _aliveStreak = new int[Offsets.BandSlots];
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];
    private readonly (byte lvl, byte br, byte fa)[] _slotId = new (byte, byte, byte)[Offsets.BandSlots];

    // Alive-edge belt: true = identity last observed alive (creditable); false = last observed dead.
    // Set true on seenAlive and on revive; set false on credit or expiry-without-actor.
    private readonly Dictionary<(byte lvl, byte br, byte fa, ushort mhp), bool> _identityAlive = new();

    /// <summary>Reset per-battle band-scan and coverage state. Called from ResetBattle (KillTracker.cs).</summary>
    private void ResetBattleCorpses()
    {
        Array.Clear(_seenAlive, 0, _seenAlive.Length);
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        Array.Clear(_aliveStreak, 0, _aliveStreak.Length);
        Array.Clear(_deadStreak, 0, _deadStreak.Length);
        Array.Clear(_slotId, 0, _slotId.Length);
        _identityAlive.Clear();
        _oracle.ResetBattle();   // enemy identities + the coverage tick/flag
    }

    /// <summary>Band corpse scan + identity capture. Returns true if the tally changed.</summary>
    private bool ScanCorpses(bool onField)
    {
        bool changed = false;
        if (onField) _oracle.TickField();   // capture enemy identities + the once-per-battle coverage log

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) { _aliveStreak[s] = 0; _deadStreak[s] = 0; continue; }

            ushort hp = _mem.U16(addr + Offsets.AHp);
            int gx = _mem.U8(addr + Offsets.AGx);
            int gy = _mem.U8(addr + Offsets.AGy);
            ushort mhp = _mem.U16(addr + Offsets.AMaxHp);
            byte lvl = _mem.U8(addr + Offsets.ALevel);
            byte br = _mem.U8(addr + Offsets.ABrave);
            byte fa = _mem.U8(addr + Offsets.AFaith);
            // Dead bit: +0x45/0x20 (Doom research). U8 returns 0 on unreadable address -- safe.
            bool deadBit = (_mem.U8(addr + Offsets.ADeadStatus) & Offsets.ADeadBit) != 0;
            bool isDead = hp == 0 || deadBit;

            if (onField) _events?.Observe(s, hp, mhp, gx, gy, _actorTag);

            if (!isDead)
            {
                _deadStreak[s] = 0;
                if (!onField) continue;
                if (_seenAlive[s] && (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa))
                {
                    _seenAlive[s] = false; _aliveStreak[s] = 0;
                    _deadCredited[s] = false; _pending[s] = false;
                    continue;
                }
                _aliveStreak[s]++;
                if (_aliveStreak[s] >= AliveNeeded && !_seenAlive[s])
                {
                    _seenAlive[s] = true;
                    _slotId[s] = (lvl, br, fa);
                    _identityAlive[(lvl, br, fa, mhp)] = true;   // alive-edge: creditable now
                }
                if (_seenAlive[s])
                {
                    // revive after a credited death: re-enable the alive-edge for this identity
                    if (_deadCredited[s]) _identityAlive[(lvl, br, fa, mhp)] = true;
                    _deadCredited[s] = false;
                    _pending[s] = false;
                }
                continue;
            }

            // dead path (hp==0 or Dead bit set)
            if (_deadCredited[s]) continue;
            if (!_seenAlive[s]) continue;
            if (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa) continue;
            if (!onField) continue;

            _deadStreak[s]++;
            if (_deadStreak[s] < DeadNeeded) continue;

            var id = (lvl, br, fa, mhp);
            if (!_oracle.Contains(id))
            {
                _deadCredited[s] = true;
                Log.Info($"kill: a unit at battle slot {s} died but it was not a tracked enemy -- no credit given (this is normal for player/guest deaths)");
                continue;
            }

            // alive-edge: credit only on alive->dead transition; a frozen twin is never alive here
            if (!_identityAlive.TryGetValue(id, out bool wasAlive) || !wasAlive)
            {
                _deadCredited[s] = true;
                Log.Info($"kill: WARN same enemy identity already credited at another slot -- blocking duplicate at battle slot {s}");
                continue;
            }

            string statusNote = deadBit && hp > 0
                ? " -- killed by status effect, waiting to see whose attack it was" : "";
            if (_lastPlayerWeapons.Count > 0)
            {
                bool c = CreditKill(s, gx, gy);
                _deadCredited[s] = true;
                _identityAlive[id] = false;   // dead now; no re-credit until revived
                if (c) changed = true;
            }
            else if (!_pending[s])
            {
                _pending[s] = true; _pendingAge[s] = 0; _pendingFalls[s] = _actedFalls;
                Log.Info($"kill: enemy down at ({gx},{gy}){statusNote} (battle slot {s})");
            }
            else if (_actedFalls - _pendingFalls[s] >= ExpireFalls || ++_pendingAge[s] > PendingTtl)
            {
                _deadCredited[s] = true; _pending[s] = false;
                _identityAlive[id] = false;
                Log.Info($"kill: could not determine who killed the enemy -- no credit (battle slot {s}, waited {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} turn-edges passed)");
            }
        }
        return changed;
    }
}
