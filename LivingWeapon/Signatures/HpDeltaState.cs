using System;

namespace LivingWeapon;

/// <summary>
/// Per-slot HP-delta event tracking, the shared core of the damage-event and heal-event
/// detectors (LW-153: <see cref="RicochetState"/> and <see cref="HealState"/> were
/// line-for-line copies with only the subtraction flipped). Baselines a slot's first sighting
/// silently; after that, a delta in the subclass's direction is one positive event and every
/// observation re-baselines. Which direction counts is the one abstract hook
/// (<see cref="Delta"/>); everything else is shared, so a fix to the baseline/consume/rearm
/// bookkeeping lands once for both detectors.
/// </summary>
internal abstract class HpDeltaState
{
    private readonly bool[] _seen;
    private readonly int[] _prevHp;

    protected HpDeltaState(int slots) { _seen = new bool[slots]; _prevHp = new int[slots]; }

    /// <summary>The direction rule: return the positive event size for this (prev, current)
    /// pair, or a value &lt;= 0 for "not this detector's event" (Observe clamps to 0).</summary>
    protected abstract int Delta(int prevHp, int currentHp);

    /// <summary>Reset on battle enter/exit.</summary>
    public void ResetBattle() { Array.Clear(_seen, 0, _seen.Length); Array.Clear(_prevHp, 0, _prevHp.Length); }

    /// <summary>Observe a slot's current HP. Returns the positive delta if this is an event in
    /// the subclass's direction on a previously-seen slot, else 0 (first sighting baselines
    /// silently; wrong-direction and same-HP re-reads return 0).</summary>
    public int Observe(int slot, int currentHp)
    {
        if (!_seen[slot])
        {
            _seen[slot] = true;
            _prevHp[slot] = currentHp;
            return 0;
        }
        int delta = Delta(_prevHp[slot], currentHp);
        _prevHp[slot] = currentHp;
        return delta > 0 ? delta : 0;
    }

    /// <summary>Record OUR OWN write as the slot's known HP, so the next Observe doesn't read
    /// it back as a fresh event (Ricochet's no-chain guarantee, Benediction's no-re-boost
    /// guarantee).</summary>
    public void Consume(int slot, int newHp)
    {
        if (_seen[slot]) _prevHp[slot] = newHp;
    }

    /// <summary>Roll a slot's baseline back to <paramref name="priorHp"/> so the NEXT Observe
    /// re-returns the same event. The un-consume half of the non-lossy event contract (Kobu):
    /// when a detected event's evaluation is blocked by a DETECTABLY-transient read (a
    /// fail-safe zero, an unwritable target, an unlocatable wielder), the caller rearms with
    /// the prior HP and retries next tick instead of permanently discarding the one-shot
    /// event. Functionally identical to <see cref="Consume"/>; kept as a delegating alias so
    /// intent reads at the call site and the two cannot drift.</summary>
    public void Rearm(int slot, int priorHp) => Consume(slot, priorHp);
}
