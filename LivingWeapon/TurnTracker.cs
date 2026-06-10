namespace LivingWeapon;

using System.Collections.Generic;

/// <summary>
/// Counts each unit's completed turns in the current battle, for TIMED signatures (e.g.
/// Galewind's Speed +3 for the wielder's first 3 turns). On every rising edge of the global
/// "acted" flag (0x14077CA8C) it credits one turn to the ACTIVE unit, identified the same way
/// KillTracker attributes a kill: the turn-queue HP/MaxHP/level -> BAND entry (live source;
/// the static array freezes on battle restart) -> (level,brave,faith) fingerprint.
///
/// Ambiguity bail: if multiple BAND entries match the turn-queue HP/MaxHP/level but have
/// DIFFERENT (level,brave,faith) fingerprints, no turn is credited (miss beats mis-credit).
/// Same-fingerprint multiples (twin) are fine -- both entries resolve to the same unit.
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
        {
            int n = (_turns.TryGetValue(fp, out int t) ? t : 0) + 1;
            _turns[fp] = n;
            Log.Info($"turn: unit (level {fp.Item1}, brave {fp.Item2}, faith {fp.Item3}) completed a turn -- #{n} this battle");
        }
        _wasActed = acted;
    }

    /// <summary>The active (turn-queue) unit's (level,brave,faith), via a band walk. Returns false
    /// if the queue is empty/garbage, no band entry matches, or the match is ambiguous (distinct
    /// fingerprints -- miss beats mis-credit). Twin entries (same fingerprint) are fine.</summary>
    private bool TryActiveFingerprint(out (int, int, int) fp)
    {
        fp = default;
        ushort maxHp = _mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp = _mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = _mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return false;

        (int, int, int) found = default;
        bool haveFp = false;
        bool foundReal = false;   // twin filter: prefer real-position entries

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(_mem, addr)) continue;
            if (_mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (_mem.U16(addr + Offsets.AHp) != hp) continue;
            if (_mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = _mem.U8(addr + Offsets.AGx) != 0 || _mem.U8(addr + Offsets.AGy) != 0;
            // twin filter: skip (0,0) entries if we already have a real-position match
            if (foundReal && !realPos) continue;
            if (realPos && !foundReal && haveFp) { found = default; haveFp = false; foundReal = true; }
            if (realPos) foundReal = true;

            var candidate = (level, (int)_mem.U8(addr + Offsets.ABrave), (int)_mem.U8(addr + Offsets.AFaith));
            if (!haveFp) { found = candidate; haveFp = true; }
            else if (found != candidate) return false;   // distinct fingerprints -> ambiguous
        }
        if (!haveFp) return false;
        fp = found;
        return true;
    }
}
