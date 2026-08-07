using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Dragon Rod's "Wyrmblood" signature: at each of the +3 wielder's turn edges, the wielder
/// AND every ALLY within RegenSplashRadius MANHATTAN tiles regenerate their OWN
/// maxHP/WyrmbloodDiv (the vanilla Regen rate), clamped at full. EMULATED regen -- the Regen
/// status bit is unmapped and never touched; the heal is a plain guarded HP write on the
/// authoritative band entries.
///
/// MANHATTAN (not Chebyshev): at radius 1 the diagonal tile is distance 2 and stays outside
/// the splash. This is the key difference from Renewal's Chebyshev aura (pinned by the two
/// suites' mirrored diagonal tests).
///
/// The stateful machinery (activation edge, completed-turn edge, wielder locate, the ally
/// heal loop, the aggregated narration) is the shared <see cref="HealPulse"/> core (LW-153:
/// this file and Renewal.cs were ~85 token-identical lines); this class is the Wyrmblood
/// config over it, and Wyrmblood.Policy.cs keeps the pure per-module rules.
/// </summary>
internal sealed partial class Wyrmblood : ISignature
{
    private const int DragonRodId = 57;

    private readonly HealPulse _pulse;

    public Wyrmblood(Dictionary<int, WeaponMeta> meta, Dictionary<int, int> kills, TurnTracker turns, IGameMemory? mem = null)
    {
        _pulse = new HealPulse(new HealPulse.Config
        {
            WeaponId = DragonRodId,
            Radius = sig => sig.RegenSplashRadius,
            IsActive = IsActive,
            Amount = mhp => RegenAmount(mhp, Tuning.WyrmbloodDiv),
            InRange = InSplash,
            ActiveLine = "Dragon Rod at tier three is wielded on the field; the end-of-turn regeneration splash is active.",
            InactiveLine = "The regeneration splash is no longer active.",
            WielderMissWarn = "The wielder's turn ended but they could not be found in memory this tick; the regeneration splash did not fire.",
            DebugVerb = "wyrmblood regenerated",
            NoAlliesDebug = "wyrmblood turn-edge regeneration found no allies in range to mend",
            Summary = (n, total) => $"{n} {(n == 1 ? "ally" : "allies")} regenerated {total} HP at the wielder's turn end.",
        }, meta, kills, turns, mem);
    }

    void ISignature.Tick(in TickContext ctx) => _pulse.Tick(ctx.OnField);

    public void Tick(bool onField) => _pulse.Tick(onField);

    public void ResetBattle() => _pulse.ResetBattle();

    // internal for test reach (the CharmLock.Drive precedent): WyrmbloodTests' pulse pins drive
    // the shared loop directly through this module's own config.
    internal void Splash(int wgx, int wgy, int radius, int turn) => _pulse.Pulse(wgx, wgy, radius);
}
