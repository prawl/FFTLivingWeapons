using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-233 deferred-verdict half of RestartSentinel: the stash's own STATE (below) and the per-tick
/// bookkeeping (ProcessStash) that owns it, split out of RestartSentinel.cs (the 200-line refactor
/// trigger) once this mechanism landed. Holding both halves here -- not just the logic -- is what
/// makes the split a real seam rather than the same state machine spread across two files (Tick()
/// and PresentRevive, in RestartSentinel.cs, still reach into this state to clear/add/return it,
/// the same cross-file field access KillTracker's own partial-class split already relies on). See
/// RestartSentinel.cs's class doc, DEFERRED VERDICT section, for why the stash exists (the 0ms-join
/// gap) and RestartSentinel.Policy.cs's ShouldStash for the pure opening-window predicate this
/// file's TTL loop enforces stateful.
/// </summary>
internal sealed partial class RestartSentinel
{
    // Revives presented while a null is mid-flight but not yet qualified. Drained (or dropped) by
    // Tick()/ProcessStash -- see the class doc's DEFERRED VERDICT section.
    private readonly List<(int Slot, List<int> Weapons, bool ViaFallback, int TicksRemaining)> _stash = new();
    // Reused scratch buffer for Tick()'s return value -- cleared and refilled every call rather
    // than allocated fresh, since most ticks drain nothing.
    private readonly List<(int Slot, List<int> Weapons, bool ViaFallback)> _drainedThisTick = new();

    /// <summary>Decrements every stashed entry's TTL and expires (drops silently) any that hit
    /// zero; if the null streak JUST qualified this tick (and grace still holds -- "opens per the
    /// normal rule"), drains every still-live entry into <see cref="_drainedThisTick"/> instead and
    /// opens the latch, logging the retry line exactly once even when several entries drain
    /// together.</summary>
    private void ProcessStash(bool justQualified)
    {
        if (_stash.Count == 0) return;

        bool canOpen = justQualified && _battleAgeTicks > RestartSentinelPolicy.GraceTicks;
        if (!canOpen)
        {
            for (int i = _stash.Count - 1; i >= 0; i--)
            {
                var e = _stash[i];
                int remaining = e.TicksRemaining - 1;
                if (remaining <= 0) _stash.RemoveAt(i);   // the null broke before qualifying -- forgotten, exactly today's re-arm
                else _stash[i] = (e.Slot, e.Weapons, e.ViaFallback, remaining);
            }
            return;
        }

        bool wasOpen = LatchOpen;
        int drainedCount = _stash.Count;
        foreach (var e in _stash) _drainedThisTick.Add((e.Slot, e.Weapons, e.ViaFallback));
        _stash.Clear();
        _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;
        if (!wasOpen)
            LogOpenEdge($"null qualified this tick, draining {drainedCount} deferred revive(s) stashed while it was still forming, battle age {_battleAgeTicks} ticks");
    }
}
