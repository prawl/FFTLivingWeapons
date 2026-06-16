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
/// acts -- ANY ally HP rise in that span (Regen, elemental absorb, item heal, reaction heal, a
/// co-equipped Wyrmblood splash, even a revive) is boosted 30%. This is WIDER than the old
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

/// <summary>Per-slot HP tracking for the heal-event detector. Baselines the first sighting
/// silently; after that a RISE in HP is one heal event (drops are ignored). Mirrors
/// RicochetState but triggers on positive deltas instead of negative.
/// <see cref="Consume"/> records our OWN bonus write so the boost is never re-triggered
/// from our own write on the next tick.</summary>
internal sealed class HealState
{
    private readonly bool[] _seen;
    private readonly int[] _prevHp;

    public HealState(int slots) { _seen = new bool[slots]; _prevHp = new int[slots]; }

    /// <summary>Reset on battle enter/exit.</summary>
    public void ResetBattle()
    {
        Array.Clear(_seen, 0, _seen.Length);
        Array.Clear(_prevHp, 0, _prevHp.Length);
    }

    /// <summary>Observe a slot's current HP. Returns the positive heal delta if this is a
    /// RISE on a previously-seen slot, else 0 (first sighting baselines silently; drops and
    /// same-HP re-reads return 0).</summary>
    public int Observe(int slot, int currentHp)
    {
        if (!_seen[slot])
        {
            _seen[slot] = true;
            _prevHp[slot] = currentHp;
            return 0;
        }
        int delta = currentHp - _prevHp[slot];
        _prevHp[slot] = currentHp;
        return delta > 0 ? delta : 0;
    }

    /// <summary>Record OUR bonus write as the slot's known HP, so the next Observe doesn't
    /// read it back as a fresh heal event (the no-re-boost guarantee).</summary>
    public void Consume(int slot, int newHp)
    {
        if (_seen[slot]) _prevHp[slot] = newHp;
    }
}
