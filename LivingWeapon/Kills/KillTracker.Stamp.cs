using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Death-edge culprit stamping for KillTracker. The deadStreak==1 edge in KillTracker.Corpses.cs
/// takes the acted-edge latch (_lastPlayerWeapons), which the 2026-07-05 tapes showed can go stale
/// because it re-resolves once per acted-period. <see cref="StampCulprit"/> overrides it only when
/// <see cref="KillerStamp"/>'s register hypothesis is fresh and disagrees (ordering gate in
/// KillerStamp.cs). <see cref="StampCulpritFromHypothesisOnly"/> applies the same hypothesis when
/// there is no latch.
/// </summary>
internal sealed partial class KillTracker
{
    // Register's last-player-arrival snapshot, read at the deadStreak==1 stamp edge
    // (KillTracker.Corpses.cs). Built in the KillTracker.cs ctor.
    private readonly KillerStamp _killerStamp;

    /// <summary>Death-edge stamp for the player-turn path. Latch keeps the latch, Register credits
    /// the pointer-named killer, Bury leaves the kill uncredited when that killer holds no tracked
    /// weapon.</summary>
    private void StampCulprit(int s)
    {
        // Only consult the hypothesis while an acted-period is open (_periodOpen, KillTracker.cs).
        // Both 2026-07-05 tapes' kills land inside open periods, and outside one the register gate
        // stays closed (KillTrackerTests.FirstKillFallback_falls_back_to_pure_TQ_outside_any_period).
        // A dead-edge after the period's debounced fall keeps the latch credit.
        if (!_periodOpen)
        {
            _lethalActor[s] = new List<int>(_lastPlayerWeapons);   // previous behavior
            _lethalViaFallback[s] = _latchViaFallback;
            return;
        }
        // LW-63 D4. The 7-out overload checks the flags-first lane before the register snapshot.
        // viaFlags/bandSlot/moved are tape evidence only; Decide judges on hypW.
        bool has = _killerStamp.TryHypothesis(_lastResolveTick, out var hypW, out ushort hypName, out int age,
                                               out bool viaFlags, out int bandSlot, out byte moved);
        string tape = viaFlags ? $" src=flags t=1 m={moved} a=1" : "";
        string source = viaFlags ? "the turn-flags lane" : "the actor register";
        switch (KillerStamp.Policy.Decide(_lastPlayerWeapons, has, hypW))
        {
            case KillerStamp.StampKind.Latch:
                _lethalActor[s] = new List<int>(_lastPlayerWeapons);
                _lethalViaFallback[s] = _latchViaFallback;
                break;
            case KillerStamp.StampKind.Register:
                _lethalActor[s] = hypW;   // fresh list from HandsFromRoster, not an alias into register state
                _lethalViaFallback[s] = false;   // pointer- or flags-derived
                ModLogger.EventWithTrace(LogVerb.Credit,
                    $"Corrected the kill attribution: {source} names a fresher killer; crediting {string.Join(", ", hypW.ConvertAll(LogNames.Weapon))} instead of {string.Join(", ", _lastPlayerWeapons.ConvertAll(LogNames.Weapon))}.",
                    $"register-override detail (latch=[{string.Join(",", _lastPlayerWeapons)}] register=[{string.Join(",", hypW)}] nameId={hypName} arrival age {age} ticks, battle slot {s})");
                _recorder?.Invoke("kill", $"stamp-override slot={s} latch=[{string.Join(",", _lastPlayerWeapons)}] register=[{string.Join(",", hypW)}] nameId={hypName} age={age}{tape}");
                break;
            case KillerStamp.StampKind.Bury:
                _lethalUntracked[s] = viaFlags ? UntrackedReason.TurnFlags : UntrackedReason.ActorRegister;
                ModLogger.EventWithTrace(LogVerb.Kill,
                    $"{(viaFlags ? "The turn-flags lane" : "The actor register")} names a player carrying no Living Weapon as the killer; the kill goes uncredited (overriding the stale latch {string.Join(", ", _lastPlayerWeapons.ConvertAll(LogNames.Weapon))}).",
                    $"register-bury detail (latch=[{string.Join(",", _lastPlayerWeapons)}] nameId={hypName} arrival age {age} ticks, battle slot {s})");
                _recorder?.Invoke("kill", $"stamp-bury slot={s} latch=[{string.Join(",", _lastPlayerWeapons)}] nameId={hypName} age={age}{tape}");
                break;
        }
    }

    /// <summary>A corpse whose edge falls with an empty latch would go pending and credit from a
    /// possibly stale later latch; a fresh nonempty hypothesis stamps the killer now instead.</summary>
    private void StampCulpritFromHypothesisOnly(int s)
    {
        if (!_periodOpen) return;   // period gate, see StampCulprit
        if (!_killerStamp.TryHypothesis(_lastResolveTick, out var hypW, out ushort hypName, out int age,
                                         out bool viaFlags, out _, out byte moved)) return;
        if (hypW.Count == 0) return;
        _lethalActor[s] = hypW;
        _lethalViaFallback[s] = false;   // pointer- or flags-derived
        string tape = viaFlags ? $" src=flags t=1 m={moved} a=1" : "";
        ModLogger.EventWithTrace(LogVerb.Credit,
            $"{(viaFlags ? "The turn-flags lane" : "The actor register")} identified the killer where no actor was latched; crediting {string.Join(", ", hypW.ConvertAll(LogNames.Weapon))}.",
            $"register-no-latch detail (register=[{string.Join(",", hypW)}] nameId={hypName} arrival age {age} ticks, battle slot {s})");
        _recorder?.Invoke("kill", $"stamp-register-nolatch slot={s} register=[{string.Join(",", hypW)}] nameId={hypName} age={age}{tape}");
    }

    /// <summary>Death-edge stamp for the empty-latch path, where the acted-period latch
    /// resolved empty (KillTracker.Corpses.cs, _latchResolvedEmpty &amp;&amp; _latched). Only a fresh,
    /// differing, armed register hypothesis overrides (Boco's Phoenix Down kill, 2026-07-05);
    /// every other outcome keeps the ActedLatch bury. A closed period keeps the bury too.</summary>
    private void StampCulpritFromEmptyLatch(int s)
    {
        if (!_periodOpen) { _lethalUntracked[s] = UntrackedReason.ActedLatch; return; }   // closed period
        bool has = _killerStamp.TryHypothesis(_lastResolveTick, out var hypW, out ushort hypName, out int age,
                                               out bool viaFlags, out _, out byte moved);
        if (KillerStamp.Policy.Decide(_lastPlayerWeapons, has, hypW) == KillerStamp.StampKind.Register)
        {
            _lethalActor[s] = hypW;   // fresh list from HandsFromRoster, not an alias into register state
            _lethalViaFallback[s] = false;   // pointer- or flags-derived
            string tape = viaFlags ? $" src=flags t=1 m={moved} a=1" : "";
            ModLogger.EventWithTrace(LogVerb.Credit,
                $"Corrected a burial: {(viaFlags ? "the turn-flags lane" : "the actor register")} names an armed killer during an unarmed actor's turn; crediting {string.Join(", ", hypW.ConvertAll(LogNames.Weapon))} instead of leaving the kill uncredited.",
                $"register-over-empty-latch detail (register=[{string.Join(",", hypW)}] nameId={hypName} arrival age {age} ticks, battle slot {s})");
            _recorder?.Invoke("kill", $"stamp-override-empty-latch slot={s} register=[{string.Join(",", hypW)}] nameId={hypName} age={age}{tape}");
        }
        else
        {
            _lethalUntracked[s] = UntrackedReason.ActedLatch;
        }
    }
}
