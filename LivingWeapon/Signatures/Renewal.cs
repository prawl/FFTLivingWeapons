using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Mending Staff "Renewal": at each +3 wielder turn edge, the wielder and every ally within
/// RegenAuraRadius (Chebyshev, so radius 1 includes diagonals; RenewalTests pins this) heal
/// round(maxHP * Tuning.RenewalPct) of their own max HP, clamped at full. The band HP write is
/// silent: no status icon or number, Regen bit untouched.
/// Config over the shared <see cref="HealPulse"/> core; pure rules are in Renewal.Policy.cs.
/// </summary>
internal sealed partial class Renewal : ISignature
{
    private const int MendingStaffId = 61;

    private readonly HealPulse _pulse;

    public Renewal(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns, IGameMemory? mem = null)
    {
        _pulse = new HealPulse(new HealPulse.Config
        {
            WeaponId = MendingStaffId,
            Radius = sig => sig.RegenAuraRadius,
            IsActive = IsActive,
            Amount = mhp => BandHeal.HealAmount(mhp, Tuning.RenewalPct),
            InRange = InAura,
            ActiveLine = "Mending Staff at tier three is wielded on the field; the end-of-turn mending aura is active.",
            InactiveLine = "The mending aura is no longer active.",
            WielderMissWarn = "The wielder's turn ended but they could not be found in memory this tick; the mending aura did not fire.",
            DebugVerb = "renewal mended",
            NoAlliesDebug = "renewal turn-edge aura found no allies in range to mend",
            Summary = (n, total) => $"{n} {(n == 1 ? "ally was" : "allies were")} mended for {total} HP at the wielder's turn end.",
        }, meta, kills, turns, mem);
    }

    void ISignature.Tick(in TickContext ctx) => _pulse.Tick(ctx.OnField);

    public void Tick(bool onField) => _pulse.Tick(onField);

    public void ResetBattle() => _pulse.ResetBattle();

    // internal so RenewalTests can drive the pulse loop directly.
    internal void Aura(int wgx, int wgy, int radius, int turn) => _pulse.Pulse(wgx, wgy, radius);
}
