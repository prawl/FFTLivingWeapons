namespace LivingWeapon;

/// <summary>
/// Counts a unit's completed turns from its own scheduler CT.
/// It reads band entry +<see cref="Offsets.ACtTurn"/> = 0x09, not +0x25, which is the ExtraTurn write target. A turn is CT
/// at or above <see cref="TurnHi"/> followed by CT below <see cref="TurnLo"/>, the same check Maim uses for victim turns.
/// Per-unit, so it avoids the global acted-edge attribution that stalled the old TurnTracker expiry (the active struct
/// follows the cursor, not the turn owner). Spiritual Font's moved-turn edge uses this clock.
/// </summary>
internal sealed class CtTurns
{
    public const int TurnHi = 90;
    public const int TurnLo = 70;

    /// <summary>Stateless form for callers that keep their own lastCt. Used by the Maim and Plague victim-turn counters.</summary>
    public static bool IsTurn(int lastCt, int curCt) => lastCt >= TurnHi && curCt < TurnLo;

    private bool _up;

    /// <summary>Completed turns observed since the last <see cref="Reset"/>.</summary>
    public int Completed { get; private set; }

    public void Observe(int ct)
    {
        if (ct >= TurnHi) { _up = true; return; }
        if (_up && ct < TurnLo) { _up = false; Completed++; }
    }

    public void Reset()
    {
        _up = false;
        Completed = 0;
    }
}
