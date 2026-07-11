using System;
using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// KillerStamp unit suite: the pure Decide policy (no memory access) and the TryHypothesis gate
/// (driven against a REAL ActorRegister over FakeSparseMemory, mirroring ActorRegisterTests.cs's
/// idioms). Companion to KillTrackerStampTests.cs (the end-to-end KillTracker.Poll integration).
/// </summary>
public class KillerStampTests
{
    /// <summary>Point Offsets.ActorPtr at band slot <paramref name="bandIdx"/>'s combat FRAME
    /// base (mirrors ActorRegisterTests.PointAt).</summary>
    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    // TryHypothesis does not care what the resolved weapons ARE, only whether a hypothesis
    // exists -- a fixed stub keeps the gate tests focused on TryHypothesis's own conditions.
    private static readonly Func<long, List<int>> StubHands = _ => new List<int> { 42 };

    /// <summary>Seed a roster slot + band unit + frame nameId, prime the register, then arrive
    /// on it (Player bridge) -- ArrivalTick == Tick == 2 on return.</summary>
    private static ActorRegister NewRegisterWithPlayerArrival(FakeSparseMemory m, int bandIdx, int rosterSlot,
                                                               int lvl, int br, int fa, int nameId)
    {
        MemSeats.SeatBand(m, bandIdx, weapon: 0, lvl: lvl, br: br, fa: fa, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, bandIdx, nameId);
        MemSeats.SeatRoster(m, rosterSlot, lvl, br, fa, rh: 999, nameId: nameId);
        var r = new ActorRegister(m);
        r.Update();       // tick1: priming (pointer unseeded)
        PointAt(m, bandIdx);
        r.Update();       // tick2: Player arrival
        return r;
    }

    // --- Decide (pure) ---

    [Fact]
    public void Decide_no_hypothesis_is_latch()
    {
        var latch = new List<int> { 52 };
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: false, hypothesisWeapons: new List<int>());
        Assert.Equal(KillerStamp.StampKind.Latch, result);
    }

    [Fact]
    public void Decide_same_set_is_latch()
    {
        var latch = new List<int> { 52, 63 };
        var hyp = new List<int> { 63, 52 };   // order-independent
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: true, hypothesisWeapons: hyp);
        Assert.Equal(KillerStamp.StampKind.Latch, result);
    }

    [Fact]
    public void Decide_disagreeing_nonempty_is_register()
    {
        var latch = new List<int> { 52 };
        var hyp = new List<int> { 90 };
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: true, hypothesisWeapons: hyp);
        Assert.Equal(KillerStamp.StampKind.Register, result);
    }

    [Fact]
    public void Decide_empty_hypothesis_is_bury()
    {
        var latch = new List<int> { 52 };
        var hyp = new List<int>();
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: true, hypothesisWeapons: hyp);
        Assert.Equal(KillerStamp.StampKind.Bury, result);
    }

    [Fact]
    public void Decide_empty_latch_nonempty_hyp_is_register()
    {
        // Pins the pending-hole extension's semantics: an empty latch (nothing has resolved yet)
        // plus a fresh nonempty hypothesis still overrides -- Register, not Bury.
        var latch = new List<int>();
        var hyp = new List<int> { 22 };
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: true, hypothesisWeapons: hyp);
        Assert.Equal(KillerStamp.StampKind.Register, result);
    }

    [Fact]
    public void Decide_no_hypothesis_with_empty_latch_is_latch()
    {
        // The empty-latch degeneracy (LW-1's StampCulpritFromEmptyLatch, KillTracker.Stamp.cs):
        // no hypothesis at all keeps today's bury regardless of the latch itself being empty.
        var latch = new List<int>();
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: false, hypothesisWeapons: new List<int>());
        Assert.Equal(KillerStamp.StampKind.Latch, result);
    }

    [Fact]
    public void Decide_both_empty_is_latch_not_bury()
    {
        // The other empty-latch degeneracy: a fresh hypothesis that is ITSELF empty (an unarmed
        // actor is her own hypothesis) hits the SameSet check first, giving Latch, not Bury.
        // Distinct from Decide_empty_hypothesis_is_bury above, which pins a NONEMPTY latch against
        // an empty hypothesis (a real disagreement, hence Bury).
        var latch = new List<int>();
        var hyp = new List<int>();
        var result = KillerStamp.Policy.Decide(latch, hasHypothesis: true, hypothesisWeapons: hyp);
        Assert.Equal(KillerStamp.StampKind.Latch, result);
    }

    // --- TryHypothesis gates (driven via a real ActorRegister) ---

    [Fact]
    public void TryHypothesis_untrusted_register_returns_false()
    {
        var m = new FakeSparseMemory();
        var r = new ActorRegister(m);
        r.Update();   // priming only -- never trusted
        var stamp = new KillerStamp(r, StubHands);

        bool ok = stamp.TryHypothesis(lastResolveTick: 0, out var weapons, out _, out _);

        Assert.False(ok);
        Assert.Empty(weapons);
    }

    [Fact]
    public void TryHypothesis_arrival_tick_zero_returns_false()
    {
        // Trusted=true (a real pointer arrival happened) but the bridge classified Enemy, which
        // leaves the last-player snapshot untouched at its initial zero -- LastPlayerArrivalTick
        // stays 0 even though the register itself is trusted. Non-vacuous: proves the "> 0" guard
        // is a distinct clause from Trusted, not redundant with it.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, weapon: 0, lvl: 30, br: 20, fa: 20, gx: 1, gy: 1);
        MemSeats.SeatFrameNameId(m, 5, nameId: 918);   // matches no roster slot -> Enemy bridge
        var r = new ActorRegister(m);
        r.Update();
        PointAt(m, 5);
        r.Update();
        Assert.True(r.Trusted);
        Assert.Equal(RosterBridge.Enemy, r.CurrentBridge);
        var stamp = new KillerStamp(r, StubHands);

        bool ok = stamp.TryHypothesis(lastResolveTick: 0, out var weapons, out _, out _);

        Assert.False(ok);
        Assert.Empty(weapons);
    }

    [Fact]
    public void TryHypothesis_arrival_equal_to_last_resolve_tick_returns_false()
    {
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        Assert.Equal(2, r.LastPlayerArrivalTick);
        var stamp = new KillerStamp(r, StubHands);

        bool ok = stamp.TryHypothesis(lastResolveTick: 2, out var weapons, out _, out _);   // equal -- reject

        Assert.False(ok);
        Assert.Empty(weapons);
    }

    [Fact]
    public void TryHypothesis_arrival_less_than_last_resolve_tick_returns_false()
    {
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        Assert.Equal(2, r.LastPlayerArrivalTick);
        var stamp = new KillerStamp(r, StubHands);

        // The arrival (tick 2) predates a later resolve (tick 4) -- the Ownership_churn-style case
        // (KillTrackerTests.Ownership_churn_after_period_start_falls_back) this gate must keep green.
        bool ok = stamp.TryHypothesis(lastResolveTick: 4, out var weapons, out _, out _);

        Assert.False(ok);
        Assert.Empty(weapons);
    }

    [Fact]
    public void TryHypothesis_arrival_older_than_window_returns_false()
    {
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        for (int i = 0; i < Tuning.RegisterKillWindow + 1; i++) r.Update();   // age = 91, one past the window
        var stamp = new KillerStamp(r, StubHands);

        bool ok = stamp.TryHypothesis(lastResolveTick: 1, out var weapons, out _, out _);

        Assert.False(ok);
        Assert.Empty(weapons);
    }

    [Fact]
    public void TryHypothesis_boundary_age_exactly_window_returns_true()
    {
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        for (int i = 0; i < Tuning.RegisterKillWindow; i++) r.Update();   // age exactly == window -- pin the <=
        var stamp = new KillerStamp(r, StubHands);

        bool ok = stamp.TryHypothesis(lastResolveTick: 1, out var weapons, out var nameId, out var age);

        Assert.True(ok);
        Assert.Equal(Tuning.RegisterKillWindow, age);
        Assert.Equal(501, nameId);
        Assert.Equal(new List<int> { 42 }, weapons);   // StubHands' fresh list
    }

    [Fact]
    public void TryHypothesis_roster_base_zero_returns_false()
    {
        // A genuine Player arrival (rosterBase != 0) immediately followed by an ambiguous Unknown
        // arrival (a duplicated roster nameId+fingerprint) CLEARS the snapshot back to its initial
        // zero state -- rosterBase reads 0 alongside arrivalTick (the two are always stamped/
        // cleared TOGETHER by ActorRegister.Update; there is no real-register path that desyncs
        // them). This pins the `&& LastPlayerRosterBase != 0` clause explicitly via that clearing
        // behavior, distinct from TryHypothesis_arrival_tick_zero_returns_false's "never arrived"
        // scenario above.
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        Assert.NotEqual(0L, r.LastPlayerRosterBase);

        // A second roster slot shares the SAME nameId+stat fingerprint. A later arrival at a
        // DIFFERENT seat with that same fingerprint resolves Unknown (duplicated identity) and
        // clears the snapshot.
        MemSeats.SeatRoster(m, slot: 6, lvl: 99, br: 89, fa: 76, rh: 999, nameId: 501);
        MemSeats.SeatBand(m, 7, weapon: 0, lvl: 99, br: 89, fa: 76, gx: 2, gy: 2);
        MemSeats.SeatFrameNameId(m, 7, nameId: 501);
        PointAt(m, 7);
        r.Update();   // arrival on seat7 -- nameId 501 now matches TWO roster slots -> Unknown -> clears

        Assert.Equal(RosterBridge.Unknown, r.CurrentBridge);
        Assert.Equal(0L, r.LastPlayerRosterBase);
        var stamp = new KillerStamp(r, StubHands);

        bool ok = stamp.TryHypothesis(lastResolveTick: 1, out var weapons, out _, out _);

        Assert.False(ok);
        Assert.Empty(weapons);
    }

    // --- LW-63 stage 2: the flags-first hypothesis lane (D4). TryHypothesis gains an overload
    // that also surfaces the flags-lane diagnostic outs (viaFlags/bandSlot/moved) the death-edge
    // stamp tapes onto its flight payload; the pre-existing 4-out overload above stays untouched
    // (every test in this file above still compiles and passes unmodified). ---

    [Fact]
    public void TryHypothesis_flags_overload_prefers_a_qualifying_flag_owner_over_the_register()
    {
        // The flags-first hypothesis wins even when a fresh, otherwise-valid register hypothesis
        // ALSO exists -- D4's ordering (flags checked FIRST, unconditionally ahead of the
        // register snapshot).
        const int flagWeapon = 90;
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        Func<long, List<int>> handsFromRoster = _ => new List<int> { flagWeapon };
        bool FlagKiller(out long rb, out ushort nid, out int slot, out byte moved)
        { rb = 999; nid = 777; slot = 5; moved = 3; return true; }
        var stamp = new KillerStamp(r, handsFromRoster, FlagKiller);

        bool ok = stamp.TryHypothesis(lastResolveTick: 1, out var weapons, out var nameId, out var age,
                                       out bool viaFlags, out int bandSlot, out byte moved);

        Assert.True(ok);
        Assert.True(viaFlags);
        Assert.Equal(new List<int> { flagWeapon }, weapons);
        Assert.Equal((ushort)777, nameId);
        Assert.Equal(0, age);
        Assert.Equal(5, bandSlot);
        Assert.Equal((byte)3, moved);
    }

    [Fact]
    public void TryHypothesis_flags_overload_falls_through_to_register_snapshot_when_no_flag_owner()
    {
        // Plan test 13, explicit pin: no flags owner at all -- the flags-first overload falls
        // through to the EXISTING register-snapshot body (unchanged, ordering gate and all). The
        // delegate here is the REAL ActorResolver.TryResolveFlagKiller against a fixture with no
        // band entry carrying ATurnFlag==1, the same "no flags owner" shape every pre-existing
        // test in this file's TryHypothesis section above already exercises implicitly.
        var m = new FakeSparseMemory();
        var r = NewRegisterWithPlayerArrival(m, bandIdx: 5, rosterSlot: 2, lvl: 99, br: 89, fa: 76, nameId: 501);
        var resolver = new ActorResolver(m, new HashSet<int> { 42 });
        var stamp = new KillerStamp(r, StubHands, resolver.TryResolveFlagKiller);

        bool ok = stamp.TryHypothesis(lastResolveTick: 1, out var weapons, out var nameId, out var age,
                                       out bool viaFlags, out _, out _);

        Assert.True(ok);
        Assert.False(viaFlags);
        Assert.Equal((ushort)501, nameId);
        Assert.Equal(new List<int> { 42 }, weapons);   // StubHands' fresh list, the register-snapshot lane
    }
}
