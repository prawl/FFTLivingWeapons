using System;

namespace LivingWeapon;

/// <summary>The edge an engine tick crossed: a fresh battle Entered, a battle Exited, or neither.</summary>
internal enum BattleEdge { None, Entered, Exited }

/// <summary>
/// The battle in/out state machine, pure (no Mem) so it's fully unit-tested. Enter is INSTANT;
/// exit is DEBOUNCED, which is the load-bearing fix:
///   * battleMode is a cursor-tile-class encoder, NOT an "on battlefield" flag -- move-browsing
///     reads 1 for seconds every turn, Paused reads 0, and mid-battle dialogue reads 0 with a real
///     eventId. So a momentary out-of-live read is normal mid-combat and must NOT end the battle.
///   * the slot9 sentinel both STICKS on the post-battle world map (so it can't mark the end) and
///     can DROP mid-battle (so its loss can't mark the end either).
/// Enter fires the instant any enter signal appears; exit needs SUSTAINED out-of-live time
/// (<see cref="ExitDebounceSeconds"/>), with pause and real-event ticks SUSPENDING the timer (a
/// pause mid-world-map must not reset progress; a pause mid-battle must never accumulate). An
/// in-live tick zeroes the accumulator. The stuck-sentinel world map is sustained out-of-live, so
/// exit fires there -- giving the next battle a clean Enter + reset. Time comes only from the
/// passed-in DateTime (tests feed synthetic clocks; the machine never reads the wall clock).
/// </summary>
internal sealed class BattleState
{
    public const double ExitDebounceSeconds = 4.0;

    public bool In { get; private set; }
    private TimeSpan _outAccum = TimeSpan.Zero;   // contiguous out-of-live time (suspended by pause/event)
    private DateTime _lastTick;                    // for the per-tick delta; valid only while In
    private bool _haveLastTick;

    /// <summary>Sentinel arm OR a live battleMode -- the instant battle-enter signal.</summary>
    internal static bool EnterSignal(uint slot0, uint slot9, int battleMode) =>
        (slot0 == 0xFF && slot9 == 0xFFFFFFFF) || battleMode == 2 || battleMode == 4
        || (battleMode == 3 && slot0 == 0xFF);

    /// <summary>A real story event/cutscene (FFTHandsFree ScreenDetectionLogic): the EventId field
    /// aliases as the active unit's nameId during combat, so only the 1..399 band -- excluding the
    /// 0xFFFF nameId-alias sentinel -- counts as a dialogue/cutscene that should suspend the exit timer.</summary>
    internal static bool IsRealEvent(int e) => e >= 1 && e < 400 && e != 0xFFFF;

    /// <summary>Step once per engine tick with raw reads; returns the edge that fired this tick.</summary>
    public BattleEdge Step(uint slot0, uint slot9, int battleMode, bool paused, int eventId, DateTime now)
    {
        if (!In)
        {
            if (!EnterSignal(slot0, slot9, battleMode)) return BattleEdge.None;
            In = true;
            _outAccum = TimeSpan.Zero;
            _lastTick = now;
            _haveLastTick = true;
            return BattleEdge.Entered;
        }

        // In: an in-live tick zeroes the accumulator; out-of-live ticks accumulate ONLY when neither
        // paused nor in a real event (both suspend without resetting). Delta comes from the clock param.
        TimeSpan delta = _haveLastTick && now > _lastTick ? now - _lastTick : TimeSpan.Zero;
        _lastTick = now;
        _haveLastTick = true;

        if (CharmLock.InLiveBattle(slot0, battleMode))
        {
            _outAccum = TimeSpan.Zero;
            return BattleEdge.None;
        }
        if (!paused && !IsRealEvent(eventId)) _outAccum += delta;
        if (_outAccum.TotalSeconds >= ExitDebounceSeconds)
        {
            In = false;
            _haveLastTick = false;
            return BattleEdge.Exited;
        }
        return BattleEdge.None;
    }
}
