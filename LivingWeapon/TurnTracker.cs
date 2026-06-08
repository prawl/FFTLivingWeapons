namespace LivingWeapon;

using System.Collections.Generic;

/// <summary>
/// Counts each unit's completed turns in the current battle, for TIMED signatures (e.g.
/// Galewind's Speed +3 for the wielder's first 3 turns). On every rising edge of the global
/// "acted" flag (0x14077CA8C) it credits one turn to the ACTIVE unit, identified the same way
/// KillTracker attributes a kill: the turn-queue HP/MaxHP/level -> static-array slot ->
/// (level,brave,faith) fingerprint. Keyed by that fingerprint so GrowthEngine (which has each
/// roster unit's level/brave/faith) can look up its wielder's turn count.
///
/// Memory access is injected (IGameMemory) so the counting is unit-testable with no live game.
/// </summary>
internal sealed class TurnTracker
{
    private readonly IGameMemory _mem;
    private readonly Dictionary<(int level, int brave, int faith), int> _turns = new();
    private bool _wasActed;

    public TurnTracker(IGameMemory mem) => _mem = mem;

    /// <summary>Forget all turn counts. Call on battle enter and exit.</summary>
    public void ResetBattle()
    {
        _turns.Clear();
        _wasActed = false;
    }

    /// <summary>Completed turns this battle for the unit with this fingerprint (0 if none).</summary>
    public int Turns(int level, int brave, int faith) =>
        _turns.TryGetValue((level, brave, faith), out int t) ? t : 0;

    /// <summary>One tick. On the rising edge of the acted flag, credit a turn to the active unit.</summary>
    public void Poll()
    {
        bool acted = _mem.U8(Offsets.Acted) == 1;
        if (acted && !_wasActed && TryActiveFingerprint(out var fp))
            _turns[fp] = (_turns.TryGetValue(fp, out int t) ? t : 0) + 1;
        _wasActed = acted;
    }

    /// <summary>The active (turn-queue) unit's (level,brave,faith), via its array slot. False if
    /// the queue is empty/garbage or no array slot matches (then no turn is credited).</summary>
    private bool TryActiveFingerprint(out (int, int, int) fp)
    {
        fp = default;
        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return false;
        for (int a = 0; a < Offsets.NSlots; a++)
        {
            long slot = Offsets.ArrayReadBase + (long)a * Offsets.ArrayStride;
            if (_mem.U16(slot + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(slot + Offsets.AHp) != hp) continue;
            if (_mem.U8(slot + Offsets.ALevel) != level) continue;
            fp = (level, _mem.U8(slot + Offsets.ABrave), _mem.U8(slot + Offsets.AFaith));
            return true;
        }
        return false;
    }
}
