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
    /// the caller) OR any live battlefield mode. battleMode reads 0 on the world map / menus and
    /// 2/3/4 on a live battlefield (Offsets.BattleMode, "3-in-battle / 0-on-map"), so a live mode
    /// is a safe enter trigger with no world-map false positive. This mirrors InLiveBattle's own
    /// live-mode set: a frame InLiveBattle already calls in-battle must also be able to ENTER the
    /// battle. The former "mode 3 only enters when slot0==0xFF" qualifier was a 1.0-era guard whose
    /// 0xFF marker never appears in 1.5 (the marker reads 0x10), so it silently dropped the enter
    /// edge whenever a battle re-entered from the world map at mode 3 (LW-40, live-proven
    /// 2026-07-07: probe read slot0=0x10 / mode=3, InLiveBattle true but EnterSignal false).</summary>
    internal static bool EnterSignal(bool pairRisingEdge, int battleMode) =>
        pairRisingEdge || battleMode == 2 || battleMode == 3 || battleMode == 4;

    /// <summary>A genuine in-battle frame, for feeding the charm heartbeat and gating every module
    /// that writes battle memory. battleMode 2/3/4 covers active-turn frames. The slot0==0xFF
    /// in-battle marker stays set through cast/attack targeting (battleMode 1/5) where gating on
    /// {2,3,4} alone starves the beat -- but the marker CANNOT be trusted alone: QUITTING a battle
    /// leaves slot0 STUCK at 0xFF on the world map (probe-verified 2026-06-10; a normal victory
    /// clears it to 0x66), which kept battles "live" forever -- no exit edge, endless charm holds.
    /// So a marker-only frame counts as live only with an EXCUSE for battleMode reading 0:
    /// targeting modes 1/5, the pause flag, or a real event id (mid-battle dialogue).</summary>
    public static bool InLiveBattle(uint slot0, int battleMode, bool paused, int eventId) =>
        battleMode == 2 || battleMode == 3 || battleMode == 4
        || (slot0 == 0xFF && (battleMode == 1 || battleMode == 5
                              || paused || IsRealEvent(eventId)));

    /// <summary>On the live battlefield: in battle AND a live mode. slot9 stays stuck on the
    /// world-map party menu, so it can't tell combat from a menu; battleMode can (2/3/4 = live
    /// battlefield, 0 = world map / menus).</summary>
    public static bool OnField(bool inBattle, int battleMode) =>
        inBattle && (battleMode == 2 || battleMode == 3 || battleMode == 4);

    /// <summary>The in-battle "Status" card (a paused, stable menu) is open -- the equip card
    /// with the Kills line, safe to paint mid-battle. Open status card = paused submenu in the
    /// action-menu context (battleMode 3). menuCursor is the card's own cursor once open (not 3),
    /// so don't gate on it.</summary>
    public static bool StatusCardOpen(bool inBattle, int battleMode, bool paused, bool submenuOpen) =>
        inBattle && battleMode == 3 && paused && submenuOpen;

    /// <summary>Paint the equip card this tick? Either the in-battle status card is open, or we
    /// have been OFF the live battlefield for the settle window (battleMode 0 = world-map party
    /// menu / post-battle). RPM/WPM make the scan/paint fail-safe, so painting in a churny menu
    /// can't crash; the settle window just avoids needless work during a mid-combat flicker.</summary>
    public static bool ShouldPaintCard(bool statusCardOpen, bool onField, double secondsOffField, double settleSeconds) =>
        statusCardOpen || (!onField && secondsOffField > settleSeconds);

    /// <summary>A battle map is currently on screen -- the broad gate used by the Treasure Master
    /// module. True when slot9 is the stuck in-battle sentinel AND battleMode is non-zero.
    /// Covers the formation/unit-placement screen (mode 1), your turn (modes 2/3/4), enemy turns
    /// and animations (mode 1), and cast targeting (mode 5). False on the world map / party menu
    /// (mode 0) regardless of whether slot9 is still stuck from a prior battle.
    /// Preferred over InLiveBattle for the treasure gate: avoids the mode-1 flicker that
    /// previously reset the stability counter on every enemy turn.</summary>
    public static bool BattleDisplayed(uint slot9, int battleMode) =>
        slot9 == 0xFFFFFFFFu && battleMode != 0;

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
            if (!EnterSignal(pairEdge, battleMode)) return BattleEdge.None;
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

        if (InLiveBattle(slot0, battleMode, paused, eventId))
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
