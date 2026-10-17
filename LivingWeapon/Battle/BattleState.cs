using System;

namespace LivingWeapon;

/// <summary>The edge an engine tick crossed. A fresh battle Entered, a battle Exited, or neither.</summary>
internal enum BattleEdge { None, Entered, Exited }

/// <summary>
/// The battle in/out state machine, pure (no Mem) so it is unit-tested. Enter is instant, exit is
/// debounced. battleMode encodes the cursor-tile class rather than "on battlefield" (move-browsing
/// reads 1, Paused reads 0, mid-battle dialogue reads 0 with a real eventId), and the slot9 sentinel
/// sticks on the post-battle world map yet can drop mid-battle, so neither can mark the end alone.
/// Exit needs <see cref="ExitDebounceSeconds"/> of out-of-live time; pause and real-event ticks
/// suspend the timer and an in-live tick zeroes it. Time comes from the passed-in DateTime.
/// </summary>
internal sealed class BattleState
{
    public const double ExitDebounceSeconds = 4.0;

    public bool In { get; private set; }
    private TimeSpan _outAccum = TimeSpan.Zero;   // contiguous out-of-live time (suspended by pause/event)
    private DateTime _lastTick;                    // for the per-tick delta; valid only while In
    private bool _haveLastTick;
    private bool _pairWasArmed;                    // last observed sentinel-pair state (for the edge)

    /// <summary>Both battle sentinels armed at once. Slot0 at the in-battle marker
    /// (Offsets.Slot0InBattleMarker, 0x10 on 1.5, re-anchored from the pre-1.5 0xFF) and
    /// slot9 at its stuck sentinel. Only the disarmed-to-armed edge of the pair enters. As a level
    /// signal it re-entered instantly after every exit, because both sentinels stuck on the
    /// pre-1.5 post-quit world map (slot0 stayed 0xFF after a quit, probe-verified 2026-06-10; the
    /// 1.5 post-quit slot0 value is unverified). Live modes stay level signals. On 1.5 slot0
    /// sampled 0xFFFFFFFF at both battle-load churn edges of the 2026-07-21 log, so no early
    /// marker edge was seen there.</summary>
    internal static bool PairArmed(uint slot0, uint slot9) =>
        slot0 == Offsets.Slot0InBattleMarker && slot9 == 0xFFFFFFFF;

    /// <summary>The instant battle-enter signal. A fresh sentinel-pair arm (edge, computed by the
    /// caller) or any live battlefield mode. battleMode reads 0 on the world map and menus and 2/3/4
    /// on a live battlefield (Offsets.BattleMode), so a live mode has no world-map false positive.
    /// This matches InLiveBattle's live-mode set. A former "mode 3 only enters when slot0==0xFF"
    /// qualifier dropped the enter edge on 1.5, where the marker reads 0x10 (seen
    /// 2026-07-07 with slot0=0x10 / mode=3).</summary>
    internal static bool EnterSignal(bool pairRisingEdge, int battleMode) =>
        pairRisingEdge || battleMode == 2 || battleMode == 3 || battleMode == 4;

    /// <summary>A genuine in-battle frame, for the charm heartbeat and every module that writes
    /// battle memory. battleMode 2/3/4 covers active-turn frames. The slot0 marker
    /// (Offsets.Slot0InBattleMarker, 0x10 on 1.5) covers cast/attack targeting and enemy turns
    /// (battleMode 1/5). Re-anchored from the pre-1.5 0xFF; before that this arm was dead
    /// and the 2026-07-21 log shows the false exit it caused (enter 07:24:14, exit 07:24:22 at
    /// mode 1). Whether the marker persists through mode 1/5 stretches is inherited pre-1.5
    /// behavior, awaiting-live (owner slow-cast eyeball); if wrong the excuse stays dead, never
    /// wrongly live. The marker alone is not trusted, since pre-1.5 a quit left it stuck on the
    /// world map (probe-verified 2026-06-10; a victory cleared it to 0x66, and a 1.5 victory exit
    /// sampled 0x11). A marker-only frame needs an excuse for battleMode 0. Targeting modes 1/5,
    /// the pause flag, or a real event id.</summary>
    public static bool InLiveBattle(uint slot0, int battleMode, bool paused, int eventId) =>
        battleMode == 2 || battleMode == 3 || battleMode == 4
        || (slot0 == Offsets.Slot0InBattleMarker && (battleMode == 1 || battleMode == 5
                              || paused || IsRealEvent(eventId)));

    /// <summary>On the live battlefield. In battle and a live mode. slot9 stays stuck on the world-map
    /// party menu, so battleMode (2/3/4 live, 0 map/menus) tells combat from a menu.</summary>
    public static bool OnField(bool inBattle, int battleMode) =>
        inBattle && (battleMode == 2 || battleMode == 3 || battleMode == 4);

    /// <summary>The in-battle Status card (a paused, stable menu) is open, so the equip card with the
    /// Kills line can be painted. That is battleMode 3 with paused and a submenu open; menuCursor is
    /// the card's own cursor once open, so it is not gated on.</summary>
    public static bool StatusCardOpen(bool inBattle, int battleMode, bool paused, bool submenuOpen) =>
        inBattle && battleMode == 3 && paused && submenuOpen;

    /// <summary>Paint the equip card this tick? When the status card is open, or after being off the
    /// live battlefield for the settle window. The settle window only avoids work during a
    /// mid-combat flicker; RPM/WPM already make painting fail-safe.</summary>
    public static bool ShouldPaintCard(bool statusCardOpen, bool onField, double secondsOffField, double settleSeconds) =>
        statusCardOpen || (!onField && secondsOffField > settleSeconds);

    /// <summary>A battle map is on screen, the broad gate for the pre-gate module. Slot9 stuck and
    /// battleMode non-zero. That covers formation (mode 1), your turn (2/3/4), enemy turns (1) and
    /// cast targeting (5), and is false on the world map even if slot9 is stuck. Preferred over
    /// InLiveBattle there to avoid the mode-1 flicker on enemy turns.</summary>
    public static bool BattleDisplayed(uint slot9, int battleMode) =>
        slot9 == 0xFFFFFFFFu && battleMode != 0;

    /// <summary>A real story event suspends the exit timer. Any nonzero id except 0xFFFF. 0xFFFF is
    /// present on every confirmed battle exit (log 2026-06-10, event=65535). Zero is excluded
    /// (unknown semantics). The old 1..399 band was a guess; event 401 showed up mid-battle on
    /// 2026-06-10 with no visible dialogue, faked an exit and dropped a kill credit. The nameId
    /// alias only occurs in-live, which zeroes the accumulator first, so any out-of-live nonzero
    /// id reaching here is a genuine event screen.</summary>
    internal static bool IsRealEvent(int e) => e >= 1 && e != 0xFFFF;

    /// <summary>Step once per engine tick with raw reads; returns the edge that fired.
    /// <paramref name="forceExit"/> exits on the same tick, bypassing the debounce and winning
    /// over an in-live reading; PlaythroughReset uses it because an in-session New Game fires no
    /// ordinary exit edge. Its only caller is gated !inLive, so the precedence never coincides in
    /// production, but is pinned so the insertion point cannot move under the in-live early-return.
    /// A no-op while not In.</summary>
    public BattleEdge Step(uint slot0, uint slot9, int battleMode, bool paused, int eventId, DateTime now,
                           bool forceExit = false)
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

        // Forced exit bypasses the debounce and wins over an in-live reading.
        if (forceExit)
        {
            In = false;
            _haveLastTick = false;
            // Snapshot the pair so one stuck armed through the transition is not a fresh enter edge next tick.
            _pairWasArmed = PairArmed(slot0, slot9);
            return BattleEdge.Exited;
        }

        // In. An in-live tick zeroes the accumulator; out-of-live ticks accumulate only when neither
        // paused nor in a real event.
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
            // Snapshot the pair at exit so one still stuck is not a fresh edge on the next tick.
            _pairWasArmed = PairArmed(slot0, slot9);
            return BattleEdge.Exited;
        }
        return BattleEdge.None;
    }
}
