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
    private bool _pairWasArmed;                    // last observed sentinel-pair state (for the edge)

    /// <summary>Both battle sentinels armed at once. A LEVEL read of this pair cannot mean
    /// "a battle is starting": BOTH sentinels stick on the post-quit world map (slot0 stays
    /// 0xFF after a battle QUIT, probe-verified 2026-06-10; slot9 always sticks), which made a
    /// level-triggered pair re-enter instantly after every exit -- a 4-second enter/exit
    /// metronome. Only the disarmed->armed EDGE of the pair enters; live modes stay level
    /// signals (a real battle reads mode 2/4 the moment the battlefield loads).</summary>
    internal static bool PairArmed(uint slot0, uint slot9) =>
        slot0 == 0xFF && slot9 == 0xFFFFFFFF;

    /// <summary>The instant battle-enter signal: a fresh sentinel-pair arm (edge, computed by
    /// the caller) OR a live battleMode level.</summary>
    internal static bool EnterSignal(bool pairRisingEdge, uint slot0, int battleMode) =>
        pairRisingEdge || battleMode == 2 || battleMode == 4
        || (battleMode == 3 && slot0 == 0xFF);

    /// <summary>A real story event/cutscene suspends the exit timer. Contract: any nonzero id except
    /// 0xFFFF is a real event. The 0xFFFF sentinel is present on every confirmed real battle exit
    /// (log 2026-06-10: both exits show event=65535); it is NOT a story event. Zero is excluded --
    /// unknown semantics, preserves existing behavior. The old 1..399 band was guesswork: live
    /// evidence on 2026-06-10 showed event 401 mid-battle -- with NO visible dialogue, so some
    /// special screen or animation carries it -- defeating the band, faking an exit, and dropping
    /// a kill credit. The nameId alias only occurs DURING combat (in-live), which already zeroes
    /// the accumulator before the event check, so any out-of-live nonzero id that reaches here is
    /// a genuine event screen of some kind.</summary>
    internal static bool IsRealEvent(int e) => e >= 1 && e != 0xFFFF;

    /// <summary>Step once per engine tick with raw reads; returns the edge that fired this tick.</summary>
    public BattleEdge Step(uint slot0, uint slot9, int battleMode, bool paused, int eventId, DateTime now)
    {
        if (!In)
        {
            bool pairArmed = PairArmed(slot0, slot9);
            bool pairEdge = pairArmed && !_pairWasArmed;
            _pairWasArmed = pairArmed;
            if (!EnterSignal(pairEdge, slot0, battleMode)) return BattleEdge.None;
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

        if (CharmLock.InLiveBattle(slot0, battleMode, paused, eventId))
        {
            _outAccum = TimeSpan.Zero;
            return BattleEdge.None;
        }
        if (!paused && !IsRealEvent(eventId)) _outAccum += delta;
        if (_outAccum.TotalSeconds >= ExitDebounceSeconds)
        {
            In = false;
            _haveLastTick = false;
            // Snapshot the pair state AT the exit: a pair that armed mid-battle and is still
            // stuck must not read as a fresh edge on the next out-of-battle tick.
            _pairWasArmed = PairArmed(slot0, slot9);
            return BattleEdge.Exited;
        }
        return BattleEdge.None;
    }
}
