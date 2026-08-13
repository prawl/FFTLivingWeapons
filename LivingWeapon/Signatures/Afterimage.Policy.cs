namespace LivingWeapon;

/// <summary>
/// The pure stacking logic behind Swiftedge's "Afterimage" signature -- no live state, no
/// memory access. The stateful hold (the Speed write against the engine's per-turn normalize)
/// lives in GrowthEngine (HoldAfterimage), exactly as HoldTimedStat does for Galewind.
///
/// Swiftedge's damage is Speed x WP (formula 99), so accelerating Speed accelerates its damage.
/// At +3 the wielder gains +<see cref="Tuning.AfterimageSpeedPerTurn"/> Speed for every turn they
/// take, stacking up to <see cref="Tuning.AfterimageSpeedCap"/> turns' worth -- and the ramp is
/// wiped to zero the instant the wielder takes damage. Keep moving, keep blurring; get hit, start over.
/// </summary>
internal static class AfterimagePolicy
{
    /// <summary>True when the signature is configured (Afterimage set) and the kill tier is earned.
    /// Wielder resolution is main-hand-only (the gift commands from the main hand).</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.Afterimage;

    /// <summary>Advance the ramp from one observation.
    /// <paramref name="turns"/> = the wielder's completed-turn count (monotonic, from TurnTracker).
    /// <paramref name="hp"/> = the wielder's current HP (0 == unreadable: no hit, carry the last good HP).
    /// A HIT (hp dropped below the last reading) wins the tick and resets stacks to 0 even if a turn
    /// also completed. Otherwise each newly completed turn adds one stack, capped at <paramref name="cap"/>.
    /// Healing (hp rose) never resets. The first observation only baselines (stacks stay 0).</summary>
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

/// <summary>One wielder's Afterimage ramp: how many stacks are held, plus the last-seen turn count
/// and HP that drive the increment/reset edges. Reset on battle exit (GrowthEngine.ResetBattle).</summary>
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
