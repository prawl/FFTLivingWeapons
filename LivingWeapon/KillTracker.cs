using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Counts kills and attributes each to the swinging weapon. Ported from the
/// battle-tested FFTHandsFree BattleTracker:
///   * STATE-BASED death -- credit a corpse exactly once (DeadCredited), reset
///     when the slot is seen alive. Survives the move / MaxHP-flicker race that
///     defeats naive "catch HP cross 0" detection.
///   * ATTRIBUTE BY CORPSE TEAM -- an enemy slot hitting 0 HP means the last
///     player who ACTED killed it, so we credit that weapon. Crediting whoever is
///     active at corpse-time mis-fires: the game rotates the active pointer to the
///     next enemy the instant an action ends. Player corpses are an enemy's kill
///     and are ignored (the Living Weapon only cares about player kills).
///   * The acting player's weapon = roster R-hand (+0x14) of the slot whose
///     nameId (+0x230) matches the condensed turn-queue nameId (+0x04).
/// </summary>
internal sealed class KillTracker
{
    private readonly Dictionary<int, int> _kills;            // weapon id -> kill count
    private readonly bool[] _deadCredited = new bool[Offsets.NSlots];
    private int _lastPlayerWeapon = -1;

    public KillTracker(Dictionary<int, int> kills) => _kills = kills;

    /// <summary>Reset per-battle state. Call on battle enter and exit.</summary>
    public void ResetBattle()
    {
        Array.Clear(_deadCredited, 0, _deadCredited.Length);
        _lastPlayerWeapon = -1;
    }

    /// <summary>One in-battle tick. Returns true if the tally changed.</summary>
    public bool Poll()
    {
        bool changed = false;

        // Capture the acting player's weapon on the tick they act.
        byte acted = Mem.U8(Offsets.Acted);
        ushort team = Mem.U16(Offsets.TurnQueue + Offsets.TqTeam);
        ushort nameId = Mem.U16(Offsets.TurnQueue + Offsets.TqNameId);
        ushort level = Mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        ushort maxHp = Mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        if (acted == 1 && team == 0 && nameId > 0 && level >= 1 && level <= 99 && maxHp > 0)
        {
            int w = ResolveWeapon(nameId);
            if (w >= 0 && w < 0xFFFF) _lastPlayerWeapon = w;
        }

        // Scan the unit array for fresh corpses.
        for (int s = 0; s < Offsets.NSlots; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            ushort inb = Mem.U16(slot + Offsets.AInBattle);
            ushort smax = Mem.U16(slot + Offsets.AMaxHp);
            byte gx = Mem.U8(slot + Offsets.AGx);
            byte gy = Mem.U8(slot + Offsets.AGy);
            if (inb == 0 || smax == 0 || smax >= 2000 || gx > 30 || gy > 30)
                continue;                                   // empty / stale / garbage slot

            ushort hp = Mem.U16(slot + Offsets.AHp);
            if (hp == 0)                                     // KO'd
            {
                if (!_deadCredited[s])
                {
                    _deadCredited[s] = true;
                    if (s <= Offsets.EnemySlotMax && _lastPlayerWeapon >= 0)
                    {
                        _kills.TryGetValue(_lastPlayerWeapon, out int c);
                        _kills[_lastPlayerWeapon] = c + 1;
                        changed = true;
                        Log.Info($"KILL -> weapon {_lastPlayerWeapon} now {c + 1} kills (enemy at {gx},{gy})");
                    }
                }
                continue;
            }
            _deadCredited[s] = false;                        // alive -> a revive+rekill recounts
        }
        return changed;
    }

    private static int ResolveWeapon(ushort nameId)
    {
        for (int s = 0; s < Offsets.RosterSlots; s++)
        {
            long baseAddr = Offsets.RosterBase + (long)s * Offsets.RosterStride;
            if (Mem.U16(baseAddr + Offsets.RNameId) == nameId)
                return Mem.U16(baseAddr + Offsets.RRHand);
        }
        return -1;
    }
}
