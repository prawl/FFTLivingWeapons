using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-233 deferred-verdict half of RestartSentinel: the stash's own STATE (below) and the per-tick
/// bookkeeping (ProcessStash) that owns it, split out of RestartSentinel.cs (the 200-line refactor
/// trigger) once this mechanism landed. Holding both halves here -- not just the logic -- is what
/// makes the split a real seam rather than the same state machine spread across two files (Tick()
/// and PresentRevive, in RestartSentinel.cs, still reach into this state to clear/add/return it,
/// the same cross-file field access KillTracker's own partial-class split already relies on). See
/// RestartSentinelPolicy.ShouldStash's own doc for why the stash exists (the 0ms-join gap) and for
/// the pure opening-window predicate this file's TTL loop enforces stateful.
/// </summary>
internal sealed partial class RestartSentinel
{
    // Revives presented while a null is mid-flight but not yet qualified. Drained (or dropped) by
    // Tick()/ProcessStash -- see RestartSentinelPolicy.ShouldStash's doc for the deferred-verdict
    // mechanism (the 0ms-join gap) this stash exists to cover. IdentityMatch is recorded ONCE, at
    // stash time (PresentRevive already computed it against the credited/presented identity passed
    // in then) -- entries are drained per-entry below rather than as a single stash-wide grace
    // check, because a matching-identity entry may be exempt from grace while a mismatched one
    // stashed the same tick is not. CreditedIdentity/PresentedIdentity/CreditedNameId/
    // PresentedNameId (FINDING 0 + FINDING 5, 2026-08-17) ride along too, purely so the drain log
    // below can report the SAME identity evidence the direct-open path reports -- IdentityMatch
    // alone (a bare bool) would tell the flight tape THAT a retry drill matched, never WHAT it
    // actually saw, which is what FINDING 3's open premise needs answered.
    private readonly List<(int Slot, List<int> Weapons, bool ViaFallback, int TicksRemaining, bool IdentityMatch,
                            (byte lvl, byte br, byte fa, ushort mhp) CreditedIdentity,
                            (byte lvl, byte br, byte fa, ushort mhp) PresentedIdentity,
                            ushort CreditedNameId, ushort PresentedNameId)> _stash = new();
    // Reused scratch buffer for Tick()'s return value -- cleared and refilled every call rather
    // than allocated fresh, since most ticks drain nothing.
    private readonly List<(int Slot, List<int> Weapons, bool ViaFallback)> _drainedThisTick = new();

    /// <summary>Per-entry TTL/drain: if the null streak JUST qualified this tick AND this entry's
    /// own grace-or-identity gate is satisfied (<see cref="RestartSentinelPolicy.GraceTicks"/> OR
    /// the identity match recorded at stash time -- same exemption as ShouldOpenLatch/ShouldStash,
    /// see their doc), drain it into <see cref="_drainedThisTick"/>; otherwise decrement its TTL
    /// and expire (drop silently) any that hit zero. Entries drain independently -- a batch of
    /// mixed matching/mismatched entries stashed on the same tick can split between draining now
    /// and continuing to wait -- but the latch-open log line still fires at most once per tick,
    /// covering every entry that drained together.</summary>
    private void ProcessStash(bool justQualified)
    {
        if (_stash.Count == 0) return;

        bool wasOpen = LatchOpen;
        int drainedCount = 0;
        for (int i = _stash.Count - 1; i >= 0; i--)
        {
            var e = _stash[i];
            bool canOpen = justQualified && (_battleAgeTicks > RestartSentinelPolicy.GraceTicks || e.IdentityMatch);
            if (canOpen)
            {
                _drainedThisTick.Add((e.Slot, e.Weapons, e.ViaFallback));
                // FINDING 5 (2026-08-17): per-entry identity evidence, same compact tap the direct-
                // open path uses (RestartSentinel.cs's IdentityTap) -- a bare IdentityMatch bool on
                // the stash tuple would tell the tape THAT a drain matched, never WHAT it saw.
                _recorder?.Invoke("restart", $"stash-drain slot={e.Slot} {IdentityTap(e.CreditedIdentity, e.CreditedNameId, e.PresentedIdentity, e.PresentedNameId)}");
                _stash.RemoveAt(i);
                drainedCount++;
                continue;
            }

            int remaining = e.TicksRemaining - 1;
            if (remaining <= 0)
            {
                // Punch-list item 3 (2026-08-17): the null broke before qualifying -- forgotten,
                // exactly today's re-arm, but no longer SILENTLY forgotten (see this class's own
                // drain record just above and RestartSentinel.cs's stash-time record for why the
                // silence here was the one remaining hole in the owner's live retry drill).
                _recorder?.Invoke("restart", $"stash-drop slot={e.Slot} {IdentityTap(e.CreditedIdentity, e.CreditedNameId, e.PresentedIdentity, e.PresentedNameId)}");
                _stash.RemoveAt(i);
            }
            else _stash[i] = (e.Slot, e.Weapons, e.ViaFallback, remaining, e.IdentityMatch,
                              e.CreditedIdentity, e.PresentedIdentity, e.CreditedNameId, e.PresentedNameId);
        }

        if (drainedCount == 0) return;
        _latchOpenTicksRemaining = RestartSentinelPolicy.LatchTicks;
        if (!wasOpen)
            LogOpenEdge($"null qualified this tick, draining {drainedCount} deferred revive(s) stashed while it was still forming, battle age {_battleAgeTicks} ticks");
    }
}
