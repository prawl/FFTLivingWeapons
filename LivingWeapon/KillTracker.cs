using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Counts kills and attributes each to the acting player's weapon(s). Ported from the
/// FFTHandsFree active-unit resolver:
///   * STATE-BASED death -- credit a corpse exactly once (DeadCredited), reset when the
///     slot is seen alive. Survives the move / MaxHP-flicker race that defeats naive
///     "catch HP cross 0" detection. A corpse seen before its killer's `acted` flag
///     latches is held PENDING (not dropped) and credited a tick later.
///   * ATTRIBUTE BY CORPSE TEAM -- an enemy slot hitting 0 HP means the last player who
///     acted killed it, so we credit that unit's weapon(s). Player corpses are an enemy's
///     kill and are ignored (the Living Weapon only cares about player kills).
///   * THE ACTING PLAYER's weapons come from ActorResolver (HP+MaxHP+level -> battle slot
///     -> level/brave/faith fingerprint -> roster hands). A DUAL-WIELDER latches BOTH
///     hands, so one kill credits both blades; a hand holding a shield is never credited.
///
/// Memory access is injected (IGameMemory) so the attribution logic is unit-testable.
/// </summary>
internal sealed class KillTracker
{
    private const int PendingTtl = 30;   // ~3s at the 100ms poll: how long a corpse waits for an actor

    private readonly Dictionary<int, int> _kills;            // weapon id -> kill count
    private readonly IGameMemory _mem;
    private readonly ActorResolver _resolver;
    private readonly bool[] _deadCredited = new bool[Offsets.NSlots];
    private readonly bool[] _pending = new bool[Offsets.NSlots];   // corpse seen, awaiting an actor latch
    private readonly int[] _pendingAge = new int[Offsets.NSlots];  // ticks a corpse has waited
    private List<int> _lastPlayerWeapons = new();   // the acting player's weapon(s); a dual-wielder latches both

    public KillTracker(Dictionary<int, int> kills, IGameMemory mem, ISet<int> weapons)
    {
        _kills = kills;
        _mem = mem;
        _resolver = new ActorResolver(mem, weapons);
    }

    /// <summary>Reset per-battle state. Call on battle enter and exit.</summary>
    public void ResetBattle()
    {
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        Array.Clear(_pending, 0, _pending.Length);
        Array.Clear(_pendingAge, 0, _pendingAge.Length);
        _lastPlayerWeapons = new();
    }

    /// <summary>One in-battle tick. Returns true if the tally changed.</summary>
    public bool Poll()
    {
        bool changed = false;

        // Latch the acting player's weapon(s) when a player has COMPLETED an action (acted==1).
        // The condensed struct's team field is NOT a reliable "player's turn" gate -- live it
        // reads 0/1/3 for the very same active player (often stuck at 3 in a battle entered
        // straight from a save load), which silently dropped captures. The roster fingerprint
        // is the real player test: ResolveActingWeapons returns a non-empty set ONLY when the
        // active unit matches a roster slot (enemies resolve to empty), so we gate on that, not
        // team. The acted==1 gate still rejects the inter-turn flicker (cursor/preview at acted==0).
        if (_mem.U8(Offsets.Acted) == 1)
        {
            var ws = _resolver.ResolveActingWeapons();
            if (ws.Count > 0 && !ActorResolver.SameSet(ws, _lastPlayerWeapons))
            {
                _lastPlayerWeapons = ws;
                Log.Info($"active player weapon(s) -> {string.Join(",", ws)}");
            }
        }

        // Scan the unit array for fresh corpses.
        for (int s = 0; s < Offsets.NSlots; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            ushort inb = _mem.U16(slot + Offsets.AInBattle);
            ushort smax = _mem.U16(slot + Offsets.AMaxHp);
            byte gx = _mem.U8(slot + Offsets.AGx);
            byte gy = _mem.U8(slot + Offsets.AGy);
            if (inb == 0 || smax == 0 || smax >= 2000 || gx > 30 || gy > 30)
                continue;                                   // empty / stale / garbage slot

            ushort hp = _mem.U16(slot + Offsets.AHp);
            if (hp == 0)                                     // KO'd
            {
                if (_deadCredited[s]) continue;              // already settled
                if (s > Offsets.EnemySlotMax)                // a player corpse -> not a weapon's kill
                {
                    _deadCredited[s] = true;
                    continue;
                }
                // Enemy corpse. The killer's `acted` flag flips to 1 AFTER the death registers, so
                // on the 100ms loop the corpse is often seen a tick or two before the actor latches.
                // Hold it PENDING until the actor appears (then credit), rather than dropping it.
                if (_lastPlayerWeapons.Count > 0)
                {
                    foreach (int w in _lastPlayerWeapons)    // a dual-wielder credits BOTH blades on one kill
                    {
                        _kills.TryGetValue(w, out int c);
                        _kills[w] = c + 1;
                        Log.Info($"KILL -> weapon {w} now {c + 1} kills (enemy at {gx},{gy})");
                    }
                    _deadCredited[s] = true; _pending[s] = false;
                    changed = true;
                }
                else if (!_pending[s])
                {
                    _pending[s] = true; _pendingAge[s] = 0;
                    Log.Info($"enemy corpse slot {s} at ({gx},{gy}) -> pending (awaiting actor)");
                }
                else if (++_pendingAge[s] > PendingTtl)      // no actor in time -> give up
                {
                    _deadCredited[s] = true; _pending[s] = false;
                    Log.Info($"enemy corpse slot {s} expired -> uncredited (no actor)");
                }
                continue;
            }
            _deadCredited[s] = false; _pending[s] = false;   // alive -> a revive+rekill recounts
        }
        return changed;
    }
}
