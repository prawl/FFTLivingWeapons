using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>LW-63 D4. Flags-first killer lookup, same signature as
/// <see cref="ActorResolver.TryResolveFlagKiller"/> so KillTracker.cs can pass the method group directly.</summary>
internal delegate bool FlagKillerLookup(out long rosterBase, out ushort nameId, out int bandSlot, out byte moved);

/// <summary>
/// Death-edge killer hypothesis from the actor register. KillTracker.Stamp.cs consults it at the
/// deadStreak==1 stamp edge (KillTracker.Corpses.cs) instead of always trusting the acted-edge
/// weapon latch (<c>_lastPlayerWeapons</c>). Two 2026-07-05 flight tapes showed the engine actor
/// pointer (<see cref="ActorRegister"/>) naming the real killer while the latch still held the
/// previous actor's weapons.
///
/// A hypothesis is valid only if its arrival is strictly after the latch's own resolve tick.
/// KillTrackerTests.Ownership_churn_after_period_start_falls_back has the arrival at tick 3 and the
/// resolve at tick 4, so the gate rejects it and the TQ fallback governs. Charged kills, whose
/// next-owner arrival lands after the resolve, are handled earlier by
/// <c>ConsumeDelayedCulprit</c> (KillTracker.Delayed.cs).
///
/// A miss is better than a mis-credit. With no hypothesis, or one that agrees with the latch, the
/// latch stands. Only a fresh, disagreeing, non-empty hypothesis overrides it, and a fresh one
/// naming a player with no tracked weapon buries the kill.
/// </summary>
internal sealed class KillerStamp
{
    private readonly ActorRegister _register;
    private readonly Func<long, List<int>> _handsFromRoster;
    // LW-63 D4. Optional flags-first lookup. Null leaves the register snapshot as the only lane,
    // which is what pre-LW-63 call sites and the KillerStampTests.cs cases that omit it rely on.
    private readonly FlagKillerLookup? _flagKiller;

    public KillerStamp(ActorRegister register, Func<long, List<int>> handsFromRoster, FlagKillerLookup? flagKiller = null)
    {
        _register = register;
        _handsFromRoster = handsFromRoster;
        _flagKiller = flagKiller;
    }

    /// <summary>True if the register holds a hypothesis newer than the latch's resolve
    /// (<paramref name="lastResolveTick"/>): trusted, a player arrival strictly after that tick and
    /// within <see cref="Tuning.RegisterKillWindow"/>, resolved to a real roster slot.
    /// <paramref name="weapons"/> is a fresh list from <see cref="ActorResolver.HandsFromRoster"/>;
    /// <paramref name="nameId"/> and <paramref name="arrivalAge"/> are for the caller's log.</summary>
    internal bool TryHypothesis(int lastResolveTick, out List<int> weapons, out ushort nameId, out int arrivalAge)
    {
        weapons = new List<int>();
        nameId = 0;
        arrivalAge = 0;

        bool valid = _register.Trusted
            && _register.LastPlayerArrivalTick > 0
            && _register.LastPlayerArrivalTick > lastResolveTick   // ordering gate
            && (_register.Tick - _register.LastPlayerArrivalTick) <= Tuning.RegisterKillWindow
            && _register.LastPlayerRosterBase != 0;
        if (!valid) return false;

        weapons = _handsFromRoster(_register.LastPlayerRosterBase);
        nameId = _register.LastPlayerNameId;
        arrivalAge = _register.Tick - _register.LastPlayerArrivalTick;
        return true;
    }

    /// <summary>LW-63 D4. Tries the flags lane first, then falls back to the 4-out overload.
    /// When <see cref="_flagKiller"/> answers, <paramref name="viaFlags"/> is true,
    /// <paramref name="arrivalAge"/> is 0 (the stamp key's a==1 requirement already orders it, see
    /// ActorResolver.Flags.cs), and <paramref name="bandSlot"/> and <paramref name="moved"/>
    /// (Offsets.AMoved) come from the winning entry for the exit flight tape. Otherwise
    /// viaFlags=false, bandSlot=-1, moved=0.</summary>
    internal bool TryHypothesis(int lastResolveTick, out List<int> weapons, out ushort nameId, out int arrivalAge,
                                 out bool viaFlags, out int bandSlot, out byte moved)
    {
        viaFlags = false;
        bandSlot = -1;
        moved = 0;
        if (_flagKiller != null && _flagKiller(out long rosterBase, out ushort flagNameId, out int flagSlot, out byte flagMoved))
        {
            weapons = _handsFromRoster(rosterBase);
            nameId = flagNameId;
            arrivalAge = 0;
            viaFlags = true;
            bandSlot = flagSlot;
            moved = flagMoved;
            return true;
        }
        return TryHypothesis(lastResolveTick, out weapons, out nameId, out arrivalAge);
    }

    /// <summary>Stamp outcome. Keep the latch, override with the register's player, or bury the kill.</summary>
    internal enum StampKind { Latch, Register, Bury }

    /// <summary>Pure decision logic, no memory access.</summary>
    internal static class Policy
    {
        /// <summary>No hypothesis, or one matching the latch's weapon set (order-independent): Latch.
        /// A disagreeing hypothesis with no tracked weapons. Bury. A disagreeing non-empty one:
        /// Register, including when the latch is empty (KillTracker.Stamp.cs StampCulpritFromHypothesisOnly).</summary>
        internal static StampKind Decide(List<int> latchWeapons, bool hasHypothesis, List<int> hypothesisWeapons)
        {
            if (!hasHypothesis) return StampKind.Latch;
            if (ActorResolver.SameSet(latchWeapons, hypothesisWeapons)) return StampKind.Latch;
            if (hypothesisWeapons.Count == 0) return StampKind.Bury;
            return StampKind.Register;
        }
    }
}
