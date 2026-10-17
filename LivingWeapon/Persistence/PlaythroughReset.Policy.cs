namespace LivingWeapon;

/// <summary>
/// Pure predicate for the Orbonne Prayer new-game opening dialogue, plus the debounce
/// PlaythroughReset holds it against. Stateless; Engine.Tick supplies every input.
/// </summary>
internal static class PlaythroughResetPolicy
{
    /// <summary>Offsets.EventId during the Orbonne Prayer opening (confirmed 2026-07-08 0xFFFF at the
    /// menu, then 2 -&gt; 4 -&gt; 5 through the prologue). The prayer lasts minutes, so it is a stable anchor.</summary>
    public const int OpeningEventId = 2;

    /// <summary>Consecutive qualifying ticks before <see cref="PlaythroughReset"/> fires, about 1s at the
    /// 33ms poll (PollMs, Engine.cs). Filters a 1-frame EventId dip during a Continue load, since a
    /// reset changes saved data.</summary>
    public const int HoldTicks = 30;

    /// <summary>True on a tick that looks like the opening prayer outside live battle. battleMode alone
    /// also reads 0 on mid-battle dialogue frames where eventId aliases the acting unit's nameId, so
    /// inLive (BattleState.InLiveBattle) separates them.</summary>
    public static bool IsOpeningOutOfBattle(int eventId, int battleMode, bool inLive) =>
        eventId == OpeningEventId && battleMode == 0 && !inLive;
}
