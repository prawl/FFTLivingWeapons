using System;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Sanctus Staff's "Benediction" signature -- no memory access.
/// The stateful band-walk and the guarded HP write live in Benediction.cs.
///
/// GATE (in Benediction.cs, not here): the sticky last-player-actor latch -- the boost is active
/// while KillTracker.LastPlayerMainHand == the Sanctus Staff. There is NO timing window: a charged
/// Cure's HP write lands ~7 s after the wielder selects it, so the latch (which persists across enemy
/// turns until the next PLAYER acts) is the only gate that survives the gap. Trade-off, stated
/// honestly: the boost is live for the ENTIRE span from the wielder's action until the next player
/// acts -- ANY ally HP rise in that span (Regen, elemental absorb, item heal, reaction heal,
/// even a revive) is boosted 30%. This is WIDER than the old
/// Acted-windowed exposure and is accepted as the cost of supporting charged spells. Common MISS
/// (fail-safe): if another player acts before the charged heal lands, the latch moves off the Sanctus
/// Staff and the boost is silently lost -- never a wrong-target boost. Revive safety: we observe HP
/// AFTER the engine applies the heal, so a revived ally already reads alive and IS boosted; only a
/// unit still reading 0 at scan time is skipped (NewHp's hp&lt;=0 guard never fires on the revive
/// path because hp is already positive by the time we observe).
///
/// NOTE on boost semantics: HealBoostPct is computed on the OBSERVED restored HP, not the
/// spell's nominal output. An overheal (heal that tops the target off) yields no bonus because
/// the observed delta after the engine clamps is zero or small. This is a deliberate design
/// choice: no overheal inflation. A future tuner who wants to compute off the nominal heal
/// would need engine-side heal-amount reads that are not currently in scope.
/// </summary>
internal sealed partial class Benediction
{
    /// <summary>True when the signature is configured (HealBoostPct > 0) and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.HealBoostPct > 0;

    /// <summary>Bonus HP to add to an observed heal of <paramref name="delta"/> HP, at
    /// <paramref name="pct"/>%. Integer floor. Returns 0 when delta &lt;= 0 (no heal event).
    /// Unlike ChipDamage, there is no floor-1 for small deltas: 0% of a 1-HP heal is 0
    /// (the scale is additive, so a genuine zero bonus is correct).</summary>
    public static int BonusHeal(int delta, int pct)
    {
        if (delta <= 0) return 0;
        return delta * pct / 100;
    }
}

/// <summary>Per-slot HP tracking for the heal-event detector: a RISE in HP is one heal event
/// (drops are ignored). The bookkeeping (baseline / consume / rearm) is the shared
/// <see cref="HpDeltaState"/> core (LW-153: this class was a line-for-line sign-flipped copy
/// of <see cref="RicochetState"/>); this subclass fixes the direction, and the heal side gains
/// the non-lossy Rearm contract for free when it ever needs it.</summary>
internal sealed class HealState : HpDeltaState
{
    public HealState(int slots) : base(slots) { }

    protected override int Delta(int prevHp, int currentHp) => currentHp - prevHp;
}
