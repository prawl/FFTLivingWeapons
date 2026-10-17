using System;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for Sanctus Staff's Benediction signature, no memory access. The band walk and
/// the guarded HP write live in Benediction.cs.
/// The gate is the sticky last-player-actor latch (KillTracker.LastPlayerMainHand == Sanctus Staff),
/// with no timing window, since a charged Cure lands about 7 s after selection. The cost is that any
/// ally HP rise between the wielder's action and the next player's action is boosted 30%. If another
/// player acts first the boost is lost, never misdirected. HP is observed after the engine applies
/// the heal, so a revived ally is already positive and is boosted.
/// HealBoostPct applies to the observed restored HP, not the spell's nominal output, so overheal
/// adds nothing. Using the nominal heal would need engine-side heal-amount reads.
/// </summary>
internal sealed partial class Benediction
{
    /// <summary>True when the signature is configured (HealBoostPct > 0) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.HealBoostPct > 0;

    /// <summary>Bonus HP for an observed heal of <paramref name="delta"/> at <paramref name="pct"/>%,
    /// integer floor, 0 when delta &lt;= 0. Unlike ChipDamage there is no floor of 1.</summary>
    public static int BonusHeal(int delta, int pct)
    {
        if (delta <= 0) return 0;
        return delta * pct / 100;
    }
}

/// <summary>Per-slot HP tracking for the heal detector. A rise in HP is one heal event, drops are
/// ignored. Bookkeeping is the shared <see cref="HpDeltaState"/> core (LW-153, formerly a
/// sign-flipped copy of <see cref="RicochetState"/>); this subclass fixes the direction.</summary>
internal sealed class HealState : HpDeltaState
{
    public HealState(int slots) : base(slots) { }

    protected override int Delta(int prevHp, int currentHp) => currentHp - prevHp;
}
