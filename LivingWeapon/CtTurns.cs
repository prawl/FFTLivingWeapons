namespace LivingWeapon;

/// <summary>
/// Counts a unit's COMPLETED turns off its OWN scheduler CT (band entry
/// +<see cref="Offsets.ACtTurn"/> = 0x09, the READ-PROVEN byte -- NOT +0x25 which is the
/// ExtraTurn WRITE target): a turn = CT seen at/above <see cref="TurnHi"/> (the turn came)
/// followed by CT below <see cref="TurnLo"/> (it was taken) -- Maim's victim-turn discipline
/// (proven live). Pure and per-unit, so it is immune to the global acted-edge attribution that
/// stalled the original TurnTracker-based expiry live (every edge credited one fingerprint;
/// the active struct follows the CURSOR, not the turn owner). Spiritual Font's moved-turn edge
/// rides this clock.
/// </summary>
internal sealed class CtTurns
{
    public const int TurnHi = 90;
    public const int TurnLo = 70;

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
