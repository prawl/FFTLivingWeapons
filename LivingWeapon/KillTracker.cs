using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Counts kills and attributes each to the acting player's weapon. Ported from the
/// FFTHandsFree active-unit resolver (NavigationActions.Scan, lines 953 + 1076):
///   * STATE-BASED death -- credit a corpse exactly once (DeadCredited), reset
///     when the slot is seen alive. Survives the move / MaxHP-flicker race that
///     defeats naive "catch HP cross 0" detection. A corpse seen before its killer's
///     `acted` flag latches is held PENDING (not dropped) and credited a tick later.
///   * ATTRIBUTE BY CORPSE TEAM -- an enemy slot hitting 0 HP means the last
///     player who acted killed it, so we credit that weapon. Player corpses are an
///     enemy's kill and are ignored (the Living Weapon only cares about player kills).
///   * THE ACTING PLAYER, the reliable way -- the condensed active struct's +0x04
///     "nameId" is a SEQUENTIAL battle index, NOT the roster nameId; it collides (a
///     Time Mage's index 1 == Ramza's roster nameId 1) and mis-credited everyone's
///     kills to Ramza. Instead: read the active struct's HP + MaxHP + level, find the
///     battle-array slot that matches, read its level/brave/faith, and fingerprint THAT
///     to the roster -> the acting unit's R-hand weapon. (The struct's team field is
///     unreliable -- reads 0/1/3 for one player -- so the roster match, not team, is the
///     player test.)
///
/// Memory access is injected (IGameMemory) so the attribution logic is unit-testable.
/// </summary>
internal sealed class KillTracker
{
    private const int PendingTtl = 30;   // ~3s at the 100ms poll: how long a corpse waits for an actor

    private readonly Dictionary<int, int> _kills;            // weapon id -> kill count
    private readonly IGameMemory _mem;
    private readonly bool[] _deadCredited = new bool[Offsets.NSlots];
    private readonly bool[] _pending = new bool[Offsets.NSlots];   // corpse seen, awaiting an actor latch
    private readonly int[] _pendingAge = new int[Offsets.NSlots];  // ticks a corpse has waited
    private int _lastPlayerWeapon = -1;

    public KillTracker(Dictionary<int, int> kills, IGameMemory mem)
    {
        _kills = kills;
        _mem = mem;
    }

    /// <summary>Reset per-battle state. Call on battle enter and exit.</summary>
    public void ResetBattle()
    {
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        Array.Clear(_pending, 0, _pending.Length);
        Array.Clear(_pendingAge, 0, _pendingAge.Length);
        _lastPlayerWeapon = -1;
    }

    /// <summary>One in-battle tick. Returns true if the tally changed.</summary>
    public bool Poll()
    {
        bool changed = false;

        // Latch the acting player's weapon when a player has COMPLETED an action (acted==1).
        // The condensed struct's team field is NOT a reliable "player's turn" gate -- live it
        // reads 0/1/3 for the very same active player (often stuck at 3 in a battle entered
        // straight from a save load), which silently dropped captures. The roster fingerprint
        // is the real player test: ResolveActiveWeapon returns >=0 ONLY when the active unit
        // matches a roster slot (enemies resolve to -1), so we gate on that, not team. The
        // acted==1 gate still rejects the inter-turn flicker (cursor/preview at acted==0).
        if (_mem.U8(Offsets.Acted) == 1)
        {
            int w = ResolveActiveWeapon();
            if (w >= 0 && w != _lastPlayerWeapon)
            {
                _lastPlayerWeapon = w;
                Log.Info($"active player weapon -> {w}");
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
                if (_lastPlayerWeapon >= 0)
                {
                    _kills.TryGetValue(_lastPlayerWeapon, out int c);
                    _kills[_lastPlayerWeapon] = c + 1;
                    _deadCredited[s] = true; _pending[s] = false;
                    changed = true;
                    Log.Info($"KILL -> weapon {_lastPlayerWeapon} now {c + 1} kills (enemy at {gx},{gy})");
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

    /// <summary>
    /// Identify the acting player's weapon WITHOUT the unreliable condensed nameId.
    /// active HP+MaxHP+level -> battle-array slot -> (level,brave,faith) fingerprint ->
    /// roster R-hand. Returns -1 if no player slot matches, the actor isn't a roster unit
    /// (e.g. an enemy sharing HP), or the match is ambiguous (two units share HP/MaxHP/level
    /// AND fingerprint to different weapons) -- a miss beats a mis-credit.
    /// </summary>
    private int ResolveActiveWeapon()
    {
        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99)
            return -1;

        int found = -1;
        for (int s = 0; s < Offsets.NSlots; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            ushort inb = _mem.U16(slot + Offsets.AInBattle);
            if (inb != 0 && inb != 1) continue;
            if (_mem.U16(slot + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(slot + Offsets.AHp) != hp) continue;
            if (_mem.U8(slot + Offsets.ALevel) != level) continue;
            int w = FingerprintRoster(level, _mem.U8(slot + Offsets.ABrave), _mem.U8(slot + Offsets.AFaith));
            if (w < 0) continue;                  // not a roster unit (e.g. an enemy)
            if (found < 0) found = w;
            else if (found != w) return -1;       // two distinct weapons -> ambiguous
        }
        return found;
    }

    /// <summary>Roster slot whose (level,brave,faith) matches -> its R-hand, else -1. Skips
    /// empty hands (0xFF/0xFFFF) and returns -1 on a roster collision (two slots, two weapons).</summary>
    private int FingerprintRoster(int level, int brave, int faith)
    {
        int found = -1;
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long b = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            if (_mem.U8(b + Offsets.RLevel) != level) continue;
            if (_mem.U8(b + Offsets.RBrave) != brave) continue;
            if (_mem.U8(b + Offsets.RFaith) != faith) continue;
            ushort w = _mem.U16(b + Offsets.RRHand);
            if (w == 0x00FF || w == 0xFFFF) continue;   // empty hand / monster
            if (found < 0) found = w;
            else if (found != w) return -1;
        }
        return found;
    }
}
