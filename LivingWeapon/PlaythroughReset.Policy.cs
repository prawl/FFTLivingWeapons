namespace LivingWeapon;

/// <summary>
/// LW-51 Tier-1: the pure predicate that recognizes the Orbonne Prayer new-game opening
/// dialogue, plus the debounce constant PlaythroughReset holds it against. Stateless: every
/// input comes from the caller each tick (Engine.Tick already reads eventId/battleMode/inLive
/// every tick post-arm), nothing here reads memory.
/// </summary>
internal static class PlaythroughResetPolicy
{
    /// <summary>Offsets.EventId's value during the Orbonne Prayer new-game opening dialogue
    /// (CONFIRMED live 2026-07-08: reads 0xFFFF at the menu, then climbs 2 -&gt; 4 -&gt; 5 through
    /// the prologue). The prayer itself lingers minutes, so this id is a stable reset anchor.</summary>
    public const int OpeningEventId = 2;

    /// <summary>Consecutive qualifying ticks <see cref="PlaythroughReset"/> requires before it
    /// fires: approx 1s at the engine's 33ms poll (PollMs, Engine.cs). The true trigger lingers
    /// minutes, so this debounce costs nothing there while killing a sub-second transient (e.g. a
    /// 1-frame EventId dip during a Continue load) outright. A reset is data-affecting, so a
    /// single-frame false positive must never fire it.</summary>
    public const int HoldTicks = 30;

    /// <summary>True on a tick that looks like the new-game opening prayer, out of live battle.
    /// Gated on !inLive as well as battleMode == 0: raw battleMode alone also reads 0 on
    /// mid-battle dialogue frames where eventId aliases the acting unit's nameId. inLive
    /// (BattleState.InLiveBattle) is what tells those apart from a genuine out-of-battle
    /// moment.</summary>
    public static bool IsOpeningOutOfBattle(int eventId, int battleMode, bool inLive) =>
        eventId == OpeningEventId && battleMode == 0 && !inLive;
}
