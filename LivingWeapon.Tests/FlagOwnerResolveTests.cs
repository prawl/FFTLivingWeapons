using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-63: the per-unit PSX turn-flags resolve lane (band +0x19C/D/E, PROVEN LIVE 2026-07-09;
/// docs/TODO.md LW-63). Drives <see cref="Band.FlagOwner"/> directly (the pure band walk) and
/// the three <see cref="ActorResolver"/> flags preambles via a bare resolver (no KillTracker) --
/// mirrors <c>ActorResolverUnarmedTests.cs</c>'s idiom. Companion suites: KillTrackerTests.cs's
/// own "turn-flags attribution" section (the end-to-end KillTracker.Poll drive, plan tests
/// 1/16/19), DelayedActorTests.cs (plan test 15, the delayed-culprit transitive pin),
/// KillerStampTests.cs / KillTrackerStampTests.cs (plan tests 11-14/18, the death-edge stamp
/// lane).
/// </summary>
public class FlagOwnerResolveTests
{
    private static readonly HashSet<int> Weapons = new() { 22, 37, 53, 60, 67, 84, 90 };

    private const int SlotA = Offsets.SlotsBack;       // band slot 20
    private const int SlotB = Offsets.SlotsBack + 1;   // band slot 21
    private const int SlotC = Offsets.SlotsBack + 2;   // band slot 22

    /// <summary>Seed the condensed turn-queue tuple (TQ-fallback ground truth for the
    /// fall-through fixtures below).</summary>
    private static void SeedActive(FakeSparseMemory m, int hp, int maxHp, int level)
    {
        m.U16s[Offsets.TurnQueue + Offsets.TqHp] = (ushort)hp;
        m.U16s[Offsets.TurnQueue + Offsets.TqMaxHp] = (ushort)maxHp;
        m.U16s[Offsets.TurnQueue + Offsets.TqLevel] = (ushort)level;
    }

    private static void SetTurnFlags(FakeSparseMemory m, int bandSlot, byte t, byte moved, byte acted)
    {
        long e = Band.Entry(bandSlot);
        m.U8s[e + Offsets.ATurnFlag] = t;
        m.U8s[e + Offsets.AMoved] = moved;
        m.U8s[e + Offsets.AActed] = acted;
    }

    private static void PointAt(FakeSparseMemory m, int bandIdx) =>
        m.SeedU64(Offsets.ActorPtr, (ulong)(Offsets.FrameReadBase + (long)bandIdx * Offsets.CombatStride));

    // ============================================================================================
    // Band.FlagOwner -- the pure band walk (D1/D2b).
    // ============================================================================================

    [Fact]
    public void FlagOwner_zero_t1_entries_returns_false()
    {
        // Test 2's Band-level half (P4: a zero-t record exists, e.g. the battle opener).
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        bool ok = Band.FlagOwner(m, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void FlagOwner_single_t1_real_position_entry_wins()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        bool ok = Band.FlagOwner(m, out long entry, out int slot);

        Assert.True(ok);
        Assert.Equal(Band.Entry(SlotA), entry);
        Assert.Equal(SlotA, slot);
    }

    [Fact]
    public void FlagOwner_two_t1_distinct_identity_returns_false()
    {
        // Test 3: two t=1 entries, distinct identities -> the flags lane must refuse (ambiguous;
        // miss beats mis-credit), never guess between them.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);
        MemSeats.SeatBand(m, SlotB, weapon: 67, lvl: 40, br: 55, fa: 65, gx: 6, gy: 6);
        MemSeats.SeatFrameNameId(m, SlotB, nameId: 502);
        SetTurnFlags(m, SlotB, t: 1, moved: 0, acted: 0);

        bool ok = Band.FlagOwner(m, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void FlagOwner_lone_zero_position_candidate_never_wins()
    {
        // Test 17 (D2b BLOCKER pin), first half: a frozen (0,0) twin's stale t=1 must never win
        // alone -- a (0,0) entry is simply never a candidate, so the lane falls through.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 0, gy: 0);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        bool ok = Band.FlagOwner(m, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void FlagOwner_zero_position_twin_loses_to_same_identity_real_position()
    {
        // Test 4 / test 17's second half: a same-identity (0,0) twin alongside a real-position
        // entry resolves via the real one (the (0,0) entry is filtered before candidacy, so it
        // cannot even create ambiguity against its own twin).
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 0, gy: 0);   // frozen twin
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);
        MemSeats.SeatBand(m, SlotB, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);   // real position, same identity
        MemSeats.SeatFrameNameId(m, SlotB, nameId: 501);
        SetTurnFlags(m, SlotB, t: 1, moved: 0, acted: 0);

        bool ok = Band.FlagOwner(m, out long entry, out int slot);

        Assert.True(ok);
        Assert.Equal(Band.Entry(SlotB), entry);
        Assert.Equal(SlotB, slot);
    }

    // ============================================================================================
    // ActorResolver's three flags preambles (D2/D3): TQ-only fixtures for the fall-through cases
    // (mirrors ActorResolverUnarmedTests.cs -- no ActorRegister needed to prove a refusal falls
    // through), a real ActorRegister for the D2 accepted-residual case (test 6).
    // ============================================================================================

    [Fact]
    public void TryResolveActingPlayer_falls_through_to_register_when_no_flag_owner()
    {
        // Test 2's ActorResolver half: zero t=1 entries anywhere -> the flags preamble refuses
        // and the register-first preamble (already proven, untouched by this fix) governs.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 60, fa: 70, rh: 37, nameId: 501);
        var register = new ActorRegister(m);
        register.Update();          // priming
        PointAt(m, SlotA);
        register.Update();          // trusted arrival, ArrivalTick=2

        var r = new ActorResolver(m, Weapons, register);
        r.BeginActedPeriod(3);      // strictly after the arrival

        bool ok = r.TryResolveActingPlayer(out var ws);

        Assert.True(ok);
        Assert.Equal(new List<int> { 37 }, ws);
    }

    [Fact]
    public void TryResolveActingPlayer_falls_through_when_two_flag_owners_disagree()
    {
        // Test 3's ActorResolver half. SlotC is the TQ-fallback ground truth (uninvolved in the
        // ambiguous pair) so the fall-through answer is unambiguous evidence, not a coincidence.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotC, weapon: 53, lvl: 30, br: 40, fa: 45, gx: 9, gy: 9, hp: 200, maxHp: 200);
        MemSeats.SeatRoster(m, slot: 2, lvl: 30, br: 40, fa: 45, rh: 53);
        SeedActive(m, hp: 200, maxHp: 200, level: 30);

        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);
        MemSeats.SeatBand(m, SlotB, weapon: 67, lvl: 40, br: 55, fa: 65, gx: 6, gy: 6);
        MemSeats.SeatFrameNameId(m, SlotB, nameId: 502);
        SetTurnFlags(m, SlotB, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);
        r.BeginActedPeriod(1);

        bool ok = r.TryResolveActingPlayer(out var ws);

        Assert.True(ok);
        Assert.Equal(new List<int> { 53 }, ws);
    }

    [Fact]
    public void TryResolveActingPlayer_falls_through_when_flag_owner_nameid_is_zero()
    {
        // Test 5: a t=1 owner whose frame nameId reads 0 (capture failure) fails closed, never a
        // guess -- the TQ fallback (SlotC) governs instead.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotC, weapon: 53, lvl: 30, br: 40, fa: 45, gx: 9, gy: 9, hp: 200, maxHp: 200);
        MemSeats.SeatRoster(m, slot: 2, lvl: 30, br: 40, fa: 45, rh: 53);
        SeedActive(m, hp: 200, maxHp: 200, level: 30);

        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        // No SeatFrameNameId call -> ANameId reads 0.
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);
        r.BeginActedPeriod(1);

        bool ok = r.TryResolveActingPlayer(out var ws);

        Assert.True(ok);
        Assert.Equal(new List<int> { 53 }, ws);
    }

    [Fact]
    public void TryResolveActingPlayer_falls_through_to_a_parked_register_when_flag_owner_is_an_enemy_turn()
    {
        // Test 6: the D2 ACCEPTED RESIDUAL (review finding 2). The t=1 owner is an ENEMY's turn
        // (its nameId matches no roster row), so the flags lane refuses; the register is
        // independently parked Player-bridged and stable since before the period, and its
        // answer still latches -- exactly as it does today (protected downstream by the TqTeam
        // death-edge bury and CreditGate, unchanged by this fix).
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 0, lvl: 20, br: 30, fa: 40, gx: 3, gy: 3);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 918);   // enemy nameId -- matches no roster row
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        MemSeats.SeatBand(m, SlotB, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotB, nameId: 501);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 60, fa: 70, rh: 37, nameId: 501);

        var register = new ActorRegister(m);
        register.Update();          // priming
        PointAt(m, SlotB);
        register.Update();          // arrival on SlotB, ArrivalTick=2, Player bridge, stable

        var r = new ActorResolver(m, Weapons, register);
        r.BeginActedPeriod(3);      // strictly after the arrival

        bool ok = r.TryResolveActingPlayer(out var ws);

        Assert.True(ok);
        Assert.Equal(new List<int> { 37 }, ws);
    }

    [Fact]
    public void TryResolveActingPlayer_falls_through_when_flag_owner_bridges_ambiguously()
    {
        // Test 7: a single t=1 owner whose nameId bridges to TWO roster rows (a duplicated
        // nameId) -> the flags lane refuses; the TQ fallback (SlotC) governs.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotC, weapon: 53, lvl: 30, br: 40, fa: 45, gx: 9, gy: 9, hp: 200, maxHp: 200);
        MemSeats.SeatRoster(m, slot: 2, lvl: 30, br: 40, fa: 45, rh: 53);
        SeedActive(m, hp: 200, maxHp: 200, level: 30);

        MemSeats.SeatBand(m, SlotB, weapon: 60, lvl: 40, br: 55, fa: 65, gx: 6, gy: 6);
        MemSeats.SeatFrameNameId(m, SlotB, nameId: 900);
        MemSeats.SeatRoster(m, slot: 5, lvl: 40, br: 55, fa: 65, rh: 60, nameId: 900);
        MemSeats.SeatRoster(m, slot: 6, lvl: 40, br: 55, fa: 65, rh: 90, nameId: 900);   // duplicated nameId
        SetTurnFlags(m, SlotB, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);
        r.BeginActedPeriod(1);

        bool ok = r.TryResolveActingPlayer(out var ws);

        Assert.True(ok);
        Assert.Equal(new List<int> { 53 }, ws);
    }

    [Fact]
    public void TryResolveActingFingerprint_returns_the_flag_owners_own_fp()
    {
        // Test 8: pairs with the delayed fp-gate (KillTracker.Delayed.cs's _lastActorFp compare)
        // -- the flags-named entry's own (level,brave,faith), read live off its band bytes.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 60, fa: 70, rh: 37, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);
        r.BeginActedPeriod(1);

        bool ok = r.TryResolveActingFingerprint(out var fp);

        Assert.True(ok);
        Assert.Equal((50, 60, 70), fp);
    }

    [Fact]
    public void ResolveActingMainHand_returns_the_flag_owners_tracked_right_hand()
    {
        // Test 9, positive half.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 60, fa: 70, rh: 37, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);
        r.BeginActedPeriod(1);

        Assert.Equal(37, r.ResolveActingMainHand());
    }

    [Fact]
    public void ResolveActingMainHand_returns_zero_when_flag_owner_untracked()
    {
        // Test 9, negative half: the flags-named player holds a real but UNTRACKED weapon.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 999, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 60, fa: 70, rh: 999, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);
        r.BeginActedPeriod(1);

        Assert.Equal(0, r.ResolveActingMainHand());
    }

    [Fact]
    public void Flags_lane_inert_outside_any_period()
    {
        // Test 10 (the V8 family): flags evidence is fully seeded (would answer if the lane were
        // open), but BeginActedPeriod is never called -- the lane must stay inert and the
        // unresolvable TQ tuple leaves the resolve refused, exactly as today outside any period.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, SlotA, weapon: 37, lvl: 50, br: 60, fa: 70, gx: 5, gy: 5);
        MemSeats.SeatFrameNameId(m, SlotA, nameId: 501);
        MemSeats.SeatRoster(m, slot: 0, lvl: 50, br: 60, fa: 70, rh: 37, nameId: 501);
        SetTurnFlags(m, SlotA, t: 1, moved: 0, acted: 0);

        var r = new ActorResolver(m, Weapons);   // BeginActedPeriod never called

        bool ok = r.TryResolveActingPlayer(out var ws);

        Assert.False(ok);
        Assert.Empty(ws);
    }
}
