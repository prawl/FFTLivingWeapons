using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Band corpse scan, enemy-identity capture, and belt/coverage check.
/// Partial class split from KillTracker.cs to stay under the 200-line limit.
///
/// Design:
///   * IDENTITY CAPTURE (team oracle): static-array slots s &lt;= EnemySlotMax with inb==1
///     supply (lvl,brave,faith,maxHp) into _enemyIds each onField tick. The array is live at
///     battle start; a restart freezes it but the capture already happened. Reinforcements
///     append while the array lives; post-restart reinforcements are a logged, accepted gap.
///   * BAND CORPSE SCAN replaces the old array scan. Per-slot alive/dead streaks with
///     3-tick guards defeat phantom-load transients. An alive-streak &gt;=3 marks seenAlive and
///     binds the identity; a dead-streak &gt;=3 (with seenAlive) may credit -- guarded by the
///     identity oracle and the credited-identity BELT (blocks relocated corpse / twin dupes).
///   * COVERAGE CHECK: once per battle, logs band vs array identity coverage for RE validation.
/// </summary>
internal sealed partial class KillTracker
{
    private const int AliveNeeded = 3;  // consecutive valid alive ticks before seenAlive is set
    private const int DeadNeeded = 3;   // consecutive dead ticks before a credit attempt
    private const int CoverageInterval = 150; // ~5s at 33ms; retry until passes once per battle

    // per-slot state (band slot index = 0..BandSlots-1)
    private readonly bool[] _seenAlive = new bool[Offsets.BandSlots];
    private readonly bool[] _deadCredited = new bool[Offsets.BandSlots];
    private readonly int[] _aliveStreak = new int[Offsets.BandSlots];
    private readonly int[] _deadStreak = new int[Offsets.BandSlots];
    // slot identity bound when seenAlive is set (level,brave,faith)
    private readonly (byte lvl, byte br, byte fa)[] _slotId = new (byte, byte, byte)[Offsets.BandSlots];

    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _enemyIds = new(); // identity oracle
    private readonly HashSet<(byte lvl, byte br, byte fa, ushort mhp)> _creditedIds = new(); // belt

    private int _coveragePollsLeft = CoverageInterval;
    private bool _coverageDone;

    /// <summary>Reset all per-battle band-scan state. Called from ResetBattle (KillTracker.cs).</summary>
    private void ResetBattleCorpses()
    {
        Array.Clear(_seenAlive, 0, _seenAlive.Length);
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        Array.Clear(_aliveStreak, 0, _aliveStreak.Length);
        Array.Clear(_deadStreak, 0, _deadStreak.Length);
        Array.Clear(_slotId, 0, _slotId.Length);
        _enemyIds.Clear();
        _creditedIds.Clear();
        _coveragePollsLeft = CoverageInterval;
        _coverageDone = false;
    }

    /// <summary>Band corpse scan + identity capture. Returns true if the tally changed.</summary>
    private bool ScanCorpses(bool onField)
    {
        bool changed = false;

        // identity capture: static-array enemy slots while onField (the array is live then)
        if (onField) CaptureEnemyIds();

        // coverage check: once per battle, validate band covers all captured array identities
        if (onField && !_coverageDone && --_coveragePollsLeft <= 0)
        {
            CheckCoverage();
            _coveragePollsLeft = CoverageInterval;  // retry next interval if not yet done
        }

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr))
            {
                // invalid slot: reset alive/dead streaks for this slot (slot may have gone away)
                _aliveStreak[s] = 0;
                _deadStreak[s] = 0;
                continue;
            }

            ushort hp = _mem.U16(addr + Offsets.AHp);
            int gx = _mem.U8(addr + Offsets.AGx);
            int gy = _mem.U8(addr + Offsets.AGy);
            ushort mhp = _mem.U16(addr + Offsets.AMaxHp);
            byte lvl = _mem.U8(addr + Offsets.ALevel);
            byte br = _mem.U8(addr + Offsets.ABrave);
            byte fa = _mem.U8(addr + Offsets.AFaith);

            if (onField) _events?.Observe(s, hp, mhp, gx, gy, _actorTag);   // dev timeline (quiet during loads)

            if (hp > 0)
            {
                // alive path: build alive streak (only while onField)
                _deadStreak[s] = 0;
                if (!onField) continue;

                // identity change at this slot -> reuse: reset all per-slot state
                if (_seenAlive[s] && (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa))
                {
                    _seenAlive[s] = false;
                    _aliveStreak[s] = 0;
                    _deadCredited[s] = false;
                    _pending[s] = false;
                    continue;
                }

                _aliveStreak[s]++;
                if (_aliveStreak[s] >= AliveNeeded && !_seenAlive[s])
                {
                    _seenAlive[s] = true;
                    _slotId[s] = (lvl, br, fa);
                }

                if (_seenAlive[s])
                {
                    // alive again: if THIS slot was previously credited (revive), evict from belt
                    // so the rekill counts. Only evict for this slot's credit (not a twin at another slot).
                    if (_deadCredited[s]) _creditedIds.Remove((lvl, br, fa, mhp));
                    _deadCredited[s] = false;
                    _pending[s] = false;
                }
                continue;
            }

            // dead (hp==0) path
            if (_deadCredited[s]) continue;   // already settled
            if (!_seenAlive[s]) continue;     // never seen alive -> load transient or pre-existing corpse

            // identity must still match bound slot identity (mhp can change if the slot reused)
            if (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa) continue;

            // only build dead streak while onField
            if (!onField) continue;

            _deadStreak[s]++;
            if (_deadStreak[s] < DeadNeeded) continue;   // not yet stable

            // deadStreak >= 3: attempt credit
            var id = (lvl, br, fa, mhp);

            if (!_enemyIds.Contains(id))
            {
                // unknown identity: guest / player / uncaptured reinforcement -- never credit
                _deadCredited[s] = true;
                Log.Info($"corpse slot {s} not a captured enemy -> uncredited");
                continue;
            }

            if (_creditedIds.Contains(id))
            {
                // same identity already credited at another slot -- relocated corpse or twin
                _deadCredited[s] = true;
                Log.Info($"WARN duplicate corpse identity blocked (slot {s})");
                continue;
            }

            // valid enemy corpse -- credit or pend
            if (_lastPlayerWeapons.Count > 0)
            {
                bool c = CreditKill(s, gx, gy);
                _deadCredited[s] = true;
                _creditedIds.Add(id);
                if (c) changed = true;
            }
            else if (!_pending[s])
            {
                _pending[s] = true; _pendingAge[s] = 0; _pendingFalls[s] = _actedFalls;
                Log.Info($"enemy corpse slot {s} at ({gx},{gy}) -> pending (awaiting actor)");
            }
            // real expiry: two debounced acted-falling edges; backstop: PendingTtl ticks
            else if (_actedFalls - _pendingFalls[s] >= ExpireFalls || ++_pendingAge[s] > PendingTtl)
            {
                _deadCredited[s] = true; _pending[s] = false;
                Log.Info($"enemy corpse slot {s} expired -> uncredited " +
                         $"(age {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} acted-fall edges)");
            }
        }

        return changed;
    }

    /// <summary>Collect identities from static-array enemy slots, by SANE FIELDS -- NOT the
    /// inBattle flag. Live (2026-06-09): that u16 pulses 0/1 per unit mid-battle (half the live
    /// enemies read 0 at any instant), so gating on it dropped those enemies from the oracle and
    /// their kills were refused. The slot-sign (s &lt;= EnemySlotMax) carries the team semantics;
    /// the bounds exclude the junk slots. Called each onField tick; additive: never removes
    /// during a battle (a restart freezes the array but the capture already happened).</summary>
    private void CaptureEnemyIds()
    {
        for (int s = 0; s <= Offsets.EnemySlotMax; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            byte lvl = _mem.U8(slot + Offsets.ALevel);
            byte br = _mem.U8(slot + Offsets.ABrave);
            byte fa = _mem.U8(slot + Offsets.AFaith);
            ushort mhp = _mem.U16(slot + Offsets.AMaxHp);
            if (lvl < 1 || lvl > 99 || br < 1 || br > 100 || fa < 1 || fa > 100
                || mhp < 1 || mhp >= 2000) continue;
            _enemyIds.Add((lvl, br, fa, mhp));
        }
    }

    /// <summary>Coverage invariant: each inb==1 array identity must appear as a valid band entry.
    /// Pure logging (no behavior change). Sets _coverageDone once the check passes cleanly.</summary>
    private void CheckCoverage()
    {
        int total = 0, found = 0;
        foreach (var id in _enemyIds)
        {
            total++;
            bool seen = false;
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long addr = Band.Entry(s);
                if (!Band.IsValid(_mem, addr)) continue;
                if (_mem.U8(addr + Offsets.ALevel) == id.lvl &&
                    _mem.U8(addr + Offsets.ABrave) == id.br &&
                    _mem.U8(addr + Offsets.AFaith) == id.fa &&
                    _mem.U16(addr + Offsets.AMaxHp) == id.mhp) { seen = true; break; }
            }
            if (seen) found++;
            else Log.Info($"WARN band coverage miss: identity lvl={id.lvl} br={id.br} fa={id.fa} mhp={id.mhp}");
        }
        Log.Info($"band coverage {found}/{total}");
        if (found == total) _coverageDone = true;
    }
}
