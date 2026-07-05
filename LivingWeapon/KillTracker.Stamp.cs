using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Death-edge culprit-stamp policy half of KillTracker: KillTracker.Corpses.cs's deadStreak==1
/// edge previously stamped <c>_lethalActor</c> from <c>_lastPlayerWeapons</c> (the acted-edge
/// latch) unconditionally. Live-taped 2026-07-05: the latch can go stale between the killer's
/// actor-pointer arrival and the corpse's death edge (the KillTracker latch only re-resolves once
/// per acted-period, while the engine's own actor pointer keeps moving). <see cref="StampCulprit"/>
/// consults <see cref="KillerStamp"/> (the register's last-player-arrival hypothesis, gated on
/// strictly postdating the latch's own resolve tick -- see KillerStamp.cs for the full ordering-
/// gate provenance) and only overrides the latch when the hypothesis is fresh AND disagrees;
/// <see cref="StampCulpritFromHypothesisOnly"/> extends the same hypothesis to the case where
/// there is no latch at all (an empty-latch corpse that would otherwise sit pending).
/// </summary>
internal sealed partial class KillTracker
{
    // Death-edge culprit hypothesis: the register's last-player-arrival snapshot, consulted only
    // at the deadStreak==1 stamp edge (KillTracker.Corpses.cs). Constructed in KillTracker.cs's ctor.
    private readonly KillerStamp _killerStamp;

    /// <summary>Death-edge culprit stamp (player-turn path): consult the register's last-player
    /// hypothesis; Latch = today's behavior byte-identical, Register = the pointer-named killer's
    /// weapons, Bury = the killer holds no tracked weapon (miss beats mis-credit).</summary>
    private void StampCulprit(int s)
    {
        // THE PERIOD GATE: the hypothesis is consulted only while an acted-period is OPEN
        // (_periodOpen, KillTracker.cs). Three reasons, in weight order:
        // (1) Evidence scope -- both 2026-07-05 tapes' kills land inside open acted-periods:
        //     tape 1b's period never closes (that IS the root cause this fix targets), and
        //     tape 2's blow lands ~100ms after the acted rising edge. There is no tape of a
        //     register override being right OUTSIDE a period.
        // (2) The V8 pin (KillTrackerTests.FirstKillFallback_falls_back_to_pure_TQ_outside_any_
        //     period): "the register gate is closed by construction" outside any period is the
        //     codebase-wide invariant -- these stamp paths must live inside it, not around it.
        // (3) An un-taped race this closes: a kill whose dead-edge slips past the period's
        //     debounced fall (a boundary DoT death) must keep today's latch credit -- without
        //     the gate, a fresh next-turn arrival could steal it.
        if (!_periodOpen)
        {
            _lethalActor[s] = new List<int>(_lastPlayerWeapons);   // today's behavior, byte-identical
            return;
        }
        bool has = _killerStamp.TryHypothesis(_lastResolveTick, out var hypW, out ushort hypName, out int age);
        switch (KillerStamp.Policy.Decide(_lastPlayerWeapons, has, hypW))
        {
            case KillerStamp.StampKind.Latch:
                _lethalActor[s] = new List<int>(_lastPlayerWeapons);
                break;
            case KillerStamp.StampKind.Register:
                _lethalActor[s] = hypW;   // fresh list from HandsFromRoster -- never an alias into register state
                ModLogger.Log($"kill: death-edge stamp overridden by the actor register -- latch=[{string.Join(",", _lastPlayerWeapons)}] register=[{string.Join(",", hypW)}] (nameId={hypName}, arrival age {age} ticks, slot {s})");
                _recorder?.Invoke("kill", $"stamp-override slot={s} latch=[{string.Join(",", _lastPlayerWeapons)}] register=[{string.Join(",", hypW)}] nameId={hypName} age={age}");
                break;
            case KillerStamp.StampKind.Bury:
                _lethalUntracked[s] = true;
                ModLogger.Log($"kill: death-edge stamp -- the register names a player with no tracked weapon; kill goes uncredited (latch=[{string.Join(",", _lastPlayerWeapons)}], nameId={hypName}, slot {s})");
                _recorder?.Invoke("kill", $"stamp-bury slot={s} latch=[{string.Join(",", _lastPlayerWeapons)}] nameId={hypName} age={age}");
                break;
        }
    }

    /// <summary>Pending-hole extension (adversarial-review finding 6): a corpse whose edge falls with
    /// an EMPTY latch would otherwise go pending and later credit from a possibly-stale future latch;
    /// a fresh nonempty hypothesis stamps the killer now. No hypothesis / empty hypothesis = today's
    /// behavior (pending) exactly.</summary>
    private void StampCulpritFromHypothesisOnly(int s)
    {
        if (!_periodOpen) return;   // THE PERIOD GATE -- rationale on StampCulprit above
        if (!_killerStamp.TryHypothesis(_lastResolveTick, out var hypW, out ushort hypName, out int age)) return;
        if (hypW.Count == 0) return;
        _lethalActor[s] = hypW;
        ModLogger.Log($"kill: death-edge stamp from the actor register (no latch) -- register=[{string.Join(",", hypW)}] (nameId={hypName}, arrival age {age} ticks, slot {s})");
        _recorder?.Invoke("kill", $"stamp-register-nolatch slot={s} register=[{string.Join(",", hypW)}] nameId={hypName} age={age}");
    }
}
