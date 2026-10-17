namespace LivingWeapon;

/// <summary>
/// Pure stacking logic for Swiftedge's "Afterimage" signature (no live state, no memory access); the stateful hold is GrowthEngine.HoldAfterimage, as HoldTimedStat is for Galewind.
/// Swiftedge's damage is Speed x WP (formula 99). At +3 the wielder gains +<see cref="Tuning.AfterimageSpeedPerTurn"/> Speed per turn taken, up to <see cref="Tuning.AfterimageSpeedCap"/> turns, and the ramp resets to zero on damage.
/// </summary>
internal static class AfterimagePolicy
{
    /// <summary>True when the signature is configured (Afterimage set) and the kill tier is earned. Main-hand wielder only.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.Afterimage;

    /// <summary>Advance the ramp from one observation. <paramref name="turns"/> is the wielder's completed-turn count (TurnTracker),
    /// <paramref name="hp"/> the current HP (0 = unreadable: carry the last good HP). A hit (hp below the last reading) resets stacks to 0 even if a turn completed;
    /// otherwise each new turn adds a stack up to <paramref name="cap"/>. Healing never resets. The first observation only baselines.</summary>
    public static AfterimageState Step(in AfterimageState prev, int turns, int hp, int cap)
    {
        if (!prev.Seeded) return new AfterimageState(0, turns, hp, seeded: true);

        int stacks;
        bool hit = hp > 0 && prev.LastHp > 0 && hp < prev.LastHp;
        if (hit) stacks = 0;
        else if (turns > prev.LastTurns)
        {
            stacks = prev.Stacks + (turns - prev.LastTurns);
            if (stacks > cap) stacks = cap;
        }
        else stacks = prev.Stacks;

        int lastHp = hp > 0 ? hp : prev.LastHp;   // a transient 0 read must not fake a hit
        return new AfterimageState(stacks, turns, lastHp, seeded: true);
    }

    /// <summary>The flat Speed bonus the current ramp confers.</summary>
    public static int SpeedBonus(in AfterimageState s, int perTurn) => s.Stacks * perTurn;
}

/// <summary>One wielder's Afterimage ramp: stacks held plus the last-seen turn count and HP. Reset on battle exit (GrowthEngine.ResetBattle).</summary>
internal readonly struct AfterimageState
{
    /// <summary>Stacks currently held (0..cap); the Speed bonus is Stacks x SpeedPerTurn.</summary>
    public int Stacks { get; }
    /// <summary>The wielder's completed-turn count at the last observation (edge-detects a new turn).</summary>
    public int LastTurns { get; }
    /// <summary>The wielder's HP at the last good observation (edge-detects a hit).</summary>
    public int LastHp { get; }
    /// <summary>False until the first observation baselines turns/HP (so the first tick never ramps).</summary>
    public bool Seeded { get; }

    public AfterimageState(int stacks, int lastTurns, int lastHp, bool seeded)
    {
        Stacks = stacks;
        LastTurns = lastTurns;
        LastHp = lastHp;
        Seeded = seeded;
    }

    /// <summary>The pre-battle / first-sight state: nothing held, not yet seeded.</summary>
    public static readonly AfterimageState Empty = new(0, 0, 0, false);
}
