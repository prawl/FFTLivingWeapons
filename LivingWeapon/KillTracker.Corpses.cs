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
    // Weapon set latched when this slot's dead-streak started (alive->dead transition); null = not yet stamped.
    // Copied from _lastPlayerWeapons at deadStreak 0->1 so a later re-latch cannot steal the credit.
    private readonly List<int>?[] _lethalActor = new List<int>?[Offsets.BandSlots];
    // Alive->dead edge fell during a resolved-but-untracked actor's LIVE acted-period -- credit nobody.
    // Stamped at deadStreak 0->1 when _latchResolvedEmpty && _latched (the actor was identified as a roster
    // player but held no living weapon). The && _latched guard is LOAD-BEARING: it prevents a false stamp
    // in the sticky interval after an untracked turn ends but before the next armed actor latches
    // (a corpse dying in that gap is from an unknown killer and must go pending, not be silently buried).
    //
    // NOTE: the in-period stamp (_latchResolvedEmpty && _latched) covers summons that resolve during
    // the caster's own acted-period (the witnessed IC case: damage at cast completion, acted==1). The
    // cross-turn case (kill matures after _latched falls) is handled by KillTracker.Delayed.cs via
    // _untrackedArmedTicks: the arm fires at Charging-bit clear (landing) and sets _lethalUntracked at
    // deadStreak==1 AHEAD of the Count>0 branch, so a live armed latch cannot absorb the summon's kill.
    private readonly bool[] _lethalUntracked = new bool[Offsets.BandSlots];

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
        Array.Clear(_lethalActor, 0, _lethalActor.Length);
        Array.Clear(_lethalUntracked, 0, _lethalUntracked.Length);
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
                    _lethalActor[s] = null; _lethalUntracked[s] = false;
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
                    _lethalActor[s] = null; _lethalUntracked[s] = false;
                }
                continue;
            }

            // dead path (hp==0 or Dead bit set)
            if (_deadCredited[s]) continue;
            if (!_seenAlive[s]) continue;
            if (_slotId[s].lvl != lvl || _slotId[s].br != br || _slotId[s].fa != fa) continue;
            if (!onField) continue;

            _deadStreak[s]++;
            // Stamp the actor at the alive->dead edge (deadStreak 0->1).
            // Only one branch fires per edge; both are mutually exclusive on _lastPlayerWeapons.Count.
            if (_deadStreak[s] == 1)
            {
                // Read TqTeam at the death EDGE so it captures the team at the moment of death
                // (it may flip a few ticks later). Divert ONLY on a confident non-player team
                // (1=enemy, 2=ally/guest) -- any other value (0=player, garbage, or unreadable
                // defaulting to 0) takes the normal credit path: a bad read must never suppress
                // a legit player kill (docs/LIVE_LEDGER.md row 45, TqTeam fail-safe rule). The
                // tracked-delayed path still wins: ConsumeDelayedCulprit() is checked first at
                // credit time and the no-credit branch is gated `delayed == null && _lethalUntracked[s]`.
                int team = _mem.Readable(Offsets.TurnQueue + Offsets.TqTeam, 2) ? _mem.U16(Offsets.TurnQueue + Offsets.TqTeam) : 0;
                bool nonPlayerTurn = team == 1 || team == 2;   // 1=enemy, 2=ally/guest; both are AI turns
                if (_untrackedArmedTicks > 0)
                {
                    // A cross-turn UNTRACKED charged action (a summoner's summon) is landing within its
                    // arm window: bury the kill regardless of the current live latch. FIRST (ahead of the
                    // Count>0 branch) so an armed ally that acted BETWEEN the cast and the landing cannot
                    // absorb the summon's kill (doctrine: miss beats mis-credit -- an unrelated armed kill
                    // inside the tight window degrades to a no-credit MISS, never a mis-credit). The arm is
                    // NOT consumed here, so every AoE victim maturing in the window is stamped; the window
                    // timer expires it. A real TRACKED delayed action still wins at credit time via the
                    // `delayed == null` guard below (the untracked arm only buries when nothing tracked claims it).
                    _lethalUntracked[s] = true;
                }
                else if (_lastPlayerWeapons.Count > 0)
                {
                    if (nonPlayerTurn)
                        _lethalUntracked[s] = true;   // counter/reaction during a non-player turn: the stale player latch did NOT deal this blow -- no-credit (miss beats mis-credit)
                    else
                        // Tracked actor latched on a player turn: copy weapons so a later re-latch cannot steal the credit.
                        _lethalActor[s] = new List<int>(_lastPlayerWeapons);
                }
                else if (_latchResolvedEmpty && _latched)
                {
                    // Kill edge fell during a resolved-but-untracked actor's LIVE acted-period
                    // (summoner/dancer/item-user, no living weapon): credit nobody.
                    // _latched gates out the inverse race -- a DIFFERENT armed unit that acted
                    // before its own acted edge while the empty latch is still sticky (that case
                    // must keep crediting the armed unit via the pending path, not be buried here).
                    _lethalUntracked[s] = true;
                }
            }

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

            // Prefer the delayed actor (snapshotted at commit, armed at landing) when available.
            // The explicit `delayed == null` guard on the no-credit branch below is what orders
            // delayed BEFORE _lethalUntracked: a tracked Jump/charge committer whose kill matures
            // while an untracked unit is latched still wins. The two CAN coexist on the same slot
            // (delayed is armed separately via TrackDelayed, not stamped at deadStreak==1), so the
            // guard -- not mutual exclusion -- is the load-bearing ordering (T4 pins it).
            var delayed = ConsumeDelayedCulprit();
            string statusNote = deadBit && hp > 0
                ? " -- killed by status effect, waiting to see whose attack it was" : "";
            if (delayed == null && _lethalUntracked[s])
            {
                // Killing edge was stamped during a roster player's turn with no tracked weapon.
                // Credit nobody: the summoner/dancer/item-user's AoE kill is intentionally uncredited.
                _deadCredited[s] = true; _pending[s] = false; _identityAlive[id] = false;
                Log.Info($"kill: enemy at battle slot {s} was killed by a unit holding no tracked weapon -- no credit (uncredited as designed)");
                continue;
            }
            var culprit = delayed ?? _lethalActor[s] ?? _lastPlayerWeapons;
            if (culprit.Count > 0)
            {
                if (delayed != null)
                    Log.Info($"kill: delayed-action override slot {s}: delayed=[{string.Join(",", delayed)}] stamp=[{string.Join(",", _lethalActor[s] as IEnumerable<int> ?? Array.Empty<int>())}] live=[{string.Join(",", _lastPlayerWeapons)}]");
                else if (_lethalActor[s] != null && !ActorResolver.SameSet(_lethalActor[s]!, _lastPlayerWeapons))
                    Log.Info($"kill: crediting lethal-damage actor [{string.Join(",", culprit)}] over live latch [{string.Join(",", _lastPlayerWeapons)}] at slot {s} (deadStreak={_deadStreak[s]})");
                bool c = CreditKill(s, gx, gy, culprit);
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
                _lethalActor[s] = null; _lethalUntracked[s] = false;
                _identityAlive[id] = false;
                Log.Info($"kill: could not determine who killed the enemy -- no credit (battle slot {s}, waited {_pendingAge[s]} ticks, {_actedFalls - _pendingFalls[s]} turn-edges passed)");
            }
        }
        return changed;
    }
}
