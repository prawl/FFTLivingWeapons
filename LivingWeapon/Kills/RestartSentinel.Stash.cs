using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-233 deferred-verdict half of RestartSentinel. The stash state and its per-tick bookkeeping
/// (ProcessStash), split out of RestartSentinel.cs for the 200-line trigger. Tick() and
/// PresentRevive in RestartSentinel.cs still reach into this state. See
/// RestartSentinelPolicy.ShouldStash for why the stash exists (the 0ms-join gap).
/// </summary>
internal sealed partial class RestartSentinel
{
    // Revives presented while a null is mid-flight but not yet qualified; drained or dropped by
    // ProcessStash. IdentityMatch is recorded at stash time and drained per entry, since a matching
    // entry can be exempt from grace while a mismatched one stashed the same tick is not. The
    // credited/presented identity fields (FINDING 0 + FINDING 5, 2026-08-17) let the drain log carry
    // the same evidence as the direct-open path, which a bare IdentityMatch bool could not.
    private readonly List<(int Slot, List<int> Weapons, bool ViaFallback, int TicksRemaining, bool IdentityMatch,
                            (byte lvl, byte br, byte fa, ushort mhp) CreditedIdentity,
                            (byte lvl, byte br, byte fa, ushort mhp) PresentedIdentity,
                            ushort CreditedNameId, ushort PresentedNameId)> _stash = new();
    // Scratch buffer for Tick()'s return value, reused because most ticks drain nothing.
    private readonly List<(int Slot, List<int> Weapons, bool ViaFallback)> _drainedThisTick = new();

    /// <summary>Per-entry TTL/drain. When the null streak just qualified and the entry passes its
    /// grace-or-identity gate (<see cref="RestartSentinelPolicy.GraceTicks"/> or the stashed identity
    /// match), it drains into <see cref="_drainedThisTick"/>; otherwise its TTL ticks down and it is
    /// dropped at zero. The latch-open log line fires at most once per tick.</summary>
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
                // FINDING 5 (2026-08-17): same compact identity tap as the direct-open path (IdentityTap).
                _recorder?.Invoke("restart", $"stash-drain slot={e.Slot} {IdentityTap(e.CreditedIdentity, e.CreditedNameId, e.PresentedIdentity, e.PresentedNameId)}");
                _stash.RemoveAt(i);
                drainedCount++;
                continue;
            }

            int remaining = e.TicksRemaining - 1;
            if (remaining <= 0)
            {
                // Punch-list item 3 (2026-08-17): the null broke before qualifying. The entry is
                // forgotten as before, but now logged so the retry drill can see it.
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
