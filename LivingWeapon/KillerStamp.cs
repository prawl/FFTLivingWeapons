using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>LW-63 D4: the death-edge stamp's flags-first lookup contract, mirroring
/// <see cref="ActorResolver.TryResolveFlagKiller"/>'s exact signature so KillTracker.cs can wire
/// the method group directly (<c>_resolver.TryResolveFlagKiller</c>) with no wrapping lambda.</summary>
internal delegate bool FlagKillerLookup(out long rosterBase, out ushort nameId, out int bandSlot, out byte moved);

/// <summary>
/// The death-edge culprit hypothesis (register half): consulted by KillTracker.Stamp.cs at the
/// deadStreak==1 stamp edge (KillTracker.Corpses.cs) instead of trusting the acted-edge weapon
/// latch (<c>_lastPlayerWeapons</c>) unconditionally.
///
/// PROVENANCE: two 2026-07-05 flight tapes taped the engine actor pointer (<see cref="ActorRegister"/>)
/// naming the real killer while the latch still held the previous actor's weapons, plus the
/// adversarial review's ordering-gate correction (review finding 1).
///
/// THE ORDERING GATE (the load-bearing fix): a hypothesis is valid only when the arrival
/// STRICTLY POSTDATES the latch's own resolve tick -- an arrival the latch already knew about (at
/// or before its own resolve) cannot outvote it. The pinned regression test
/// (KillTrackerTests.Ownership_churn_after_period_start_falls_back) has its arrival at tick 3 and
/// its resolve at tick 4: the arrival PREdates the resolve, so this gate rejects it and the TQ
/// fallback governs, exactly as today. Delayed-action pre-emption (<c>ConsumeDelayedCulprit</c>,
/// KillTracker.Delayed.cs) remains the separate guard for charged kills whose eager next-owner
/// arrival postdates the resolve -- that path is untouched by this class and is checked FIRST at
/// credit time (KillTracker.Corpses.cs's <c>culprit = delayed ?? _lethalActor[s] ?? ...</c> chain).
///
/// Doctrine: miss beats mis-credit. Every ambiguous case (no hypothesis, or the hypothesis agrees
/// with the latch) keeps today's behavior byte-identical; only a FRESH, DISAGREEING, non-empty
/// hypothesis overrides -- and a fresh hypothesis naming a player with NO tracked weapon buries the
/// kill rather than crediting the stale latch.
/// </summary>
internal sealed class KillerStamp
{
    private readonly ActorRegister _register;
    private readonly Func<long, List<int>> _handsFromRoster;
    // LW-63 D4: optional flags-first lookup (ActorResolver.TryResolveFlagKiller). Null default
    // (every pre-LW-63 call site, and every test in KillerStampTests.cs that omits it) keeps the
    // register-snapshot lane the ONLY lane, byte-identical to before this fix -- the injected-
    // dependency pattern this codebase uses everywhere (e.g. KillTracker's own recorder/deeds/
    // hasLiveWielder ctor params).
    private readonly FlagKillerLookup? _flagKiller;

    public KillerStamp(ActorRegister register, Func<long, List<int>> handsFromRoster, FlagKillerLookup? flagKiller = null)
    {
        _register = register;
        _handsFromRoster = handsFromRoster;
        _flagKiller = flagKiller;
    }

    /// <summary>True iff the register holds a hypothesis FRESHER than the latch's own resolve
    /// (<paramref name="lastResolveTick"/>): trusted, a player arrival exists, that arrival is
    /// STRICTLY newer than the latch's resolve (the ordering gate -- the primary correctness
    /// condition), within the coarse <see cref="Tuning.RegisterKillWindow"/> staleness backstop,
    /// and the arrival resolved to a real roster slot. On success <paramref name="weapons"/> is a
    /// FRESH list (never an alias into register state, via <see cref="ActorResolver.HandsFromRoster"/>)
    /// from the arrival's roster slot; <paramref name="nameId"/> and <paramref name="arrivalAge"/>
    /// (register ticks since arrival) are diagnostic, logged by the caller.</summary>
    internal bool TryHypothesis(int lastResolveTick, out List<int> weapons, out ushort nameId, out int arrivalAge)
    {
        weapons = new List<int>();
        nameId = 0;
        arrivalAge = 0;

        bool valid = _register.Trusted
            && _register.LastPlayerArrivalTick > 0
            && _register.LastPlayerArrivalTick > lastResolveTick   // THE ordering gate
            && (_register.Tick - _register.LastPlayerArrivalTick) <= Tuning.RegisterKillWindow
            && _register.LastPlayerRosterBase != 0;
        if (!valid) return false;

        weapons = _handsFromRoster(_register.LastPlayerRosterBase);
        nameId = _register.LastPlayerNameId;
        arrivalAge = _register.Tick - _register.LastPlayerArrivalTick;
        return true;
    }

    /// <summary>LW-63 D4: overload of <see cref="TryHypothesis(int, out List{int}, out ushort, out int)"/>
    /// that consults the flags-first lane BEFORE the register snapshot, and surfaces the flags
    /// lane's own diagnostic outs for the death-edge stamp's flight tape (review finding 5): the
    /// owner's live Verify ("a1 at the killing edge") becomes satisfiable from the exit tape.
    /// When <see cref="_flagKiller"/> is wired and answers, <paramref name="weapons"/> comes from
    /// <see cref="_handsFromRoster"/> over the flags-named roster slot, <paramref name="arrivalAge"/>
    /// is 0 (the flags lane needs no arrival-ordering gate -- the stamp key's own a==1 requirement
    /// IS its ordering gate, ActorResolver.Flags.cs), and <paramref name="viaFlags"/> is true with
    /// <paramref name="bandSlot"/>/<paramref name="moved"/> carrying the winning entry's own band
    /// slot and Offsets.AMoved byte. Otherwise this simply calls the 4-out overload above
    /// UNCHANGED (the register-snapshot body, ordering gate and all) and reports viaFlags=false /
    /// bandSlot=-1 / moved=0. Every pre-existing caller keeps using the 4-out overload; this one
    /// is additive.</summary>
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

    /// <summary>Death-edge culprit stamp outcome: keep today's latch, override with the register's
    /// named player's weapons, or bury the kill (the register names a player holding nothing
    /// tracked).</summary>
    internal enum StampKind { Latch, Register, Bury }

    /// <summary>Pure decision half (the X.cs/X.Policy.cs house seam, nested here at this file's
    /// size): no memory access, unit-tested directly.</summary>
    internal static class Policy
    {
        /// <summary>No hypothesis, or the hypothesis agrees with the latch (same weapon set,
        /// order-independent) -&gt; Latch (today's behavior, byte-identical). A fresh, disagreeing,
        /// EMPTY hypothesis -&gt; Bury (the register names a player holding no tracked weapon --
        /// miss beats mis-crediting the stale latch). A fresh, disagreeing, non-empty hypothesis
        /// -&gt; Register (including when the latch itself is empty: the pending-hole extension,
        /// KillTracker.Stamp.cs's StampCulpritFromHypothesisOnly).</summary>
        internal static StampKind Decide(List<int> latchWeapons, bool hasHypothesis, List<int> hypothesisWeapons)
        {
            if (!hasHypothesis) return StampKind.Latch;
            if (ActorResolver.SameSet(latchWeapons, hypothesisWeapons)) return StampKind.Latch;
            if (hypothesisWeapons.Count == 0) return StampKind.Bury;
            return StampKind.Register;
        }
    }
}
