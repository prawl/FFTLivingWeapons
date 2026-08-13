using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Mending Staff's "Renewal" signature: at each of the +3 wielder's turn edges, the wielder
/// AND every ALLY within RegenAuraRadius CHEBYSHEV tiles are healed for
/// round(maxHP * Tuning.RenewalPct) of their OWN max HP, clamped at full. A SILENT band HP
/// write -- no status icon, no floating number; the Regen status bit is never touched.
///
/// CHEBYSHEV (not Manhattan): radius 1 covers all 8 surrounding tiles, including diagonals.
/// A deliberate choice, pinned by RenewalTests' diagonal-reach test on the shared core's
/// injected metric.
///
/// The stateful machinery (activation edge, completed-turn edge, wielder locate, the ally
/// heal loop, the aggregated narration) is the shared <see cref="HealPulse"/> core (LW-153;
/// its class doc carries the extraction story); this class is the Renewal config over it,
/// and Renewal.Policy.cs keeps the pure per-module rules.
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

    // internal for test reach: RenewalTests' pulse pins drive
    // the shared loop directly through this module's own config.
    internal void Aura(int wgx, int wgy, int radius, int turn) => _pulse.Pulse(wgx, wgy, radius);
}
