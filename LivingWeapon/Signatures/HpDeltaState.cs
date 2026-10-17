using System;

namespace LivingWeapon;

/// <summary>
/// Tracks per-slot HP-delta events, shared by the damage-event and heal-event detectors.
/// <see cref="RicochetState"/> and <see cref="HealState"/> were copies with the subtraction flipped.
/// A slot's first sighting baselines silently; after that a delta in the subclass's direction is one
/// positive event and every observation re-baselines. <see cref="Delta"/> sets the direction.
/// </summary>
internal abstract class HpDeltaState
{
    private readonly bool[] _seen;
    private readonly int[] _prevHp;

    protected HpDeltaState(int slots) { _seen = new bool[slots]; _prevHp = new int[slots]; }

    /// <summary>Positive event size for this (prev, current) pair, or &lt;= 0 for none.</summary>
    protected abstract int Delta(int prevHp, int currentHp);

    /// <summary>Reset on battle enter/exit.</summary>
    public void ResetBattle() { Array.Clear(_seen, 0, _seen.Length); Array.Clear(_prevHp, 0, _prevHp.Length); }

    /// <summary>Returns the positive delta for an event in the subclass's direction on a seen slot,
    /// else 0 (first sightings baseline silently).</summary>
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

    /// <summary>Record our own write as the slot's known HP so Observe does not read it back as a
    /// new event (Ricochet no-chain, Benediction no-re-boost).</summary>
    public void Consume(int slot, int newHp)
    {
        if (_seen[slot]) _prevHp[slot] = newHp;
    }

    /// <summary>Roll a slot's baseline back to <paramref name="priorHp"/> so the next Observe returns
    /// the same event again (Kobu), for when a transient read blocked evaluation. Same as
    /// <see cref="Consume"/>, kept as an alias so call sites read clearly.</summary>
    public void Rearm(int slot, int priorHp) => Consume(slot, priorHp);
}
