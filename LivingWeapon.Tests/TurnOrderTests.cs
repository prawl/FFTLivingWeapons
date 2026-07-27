using System.Collections.Generic;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Pure band-walk coverage for TurnOrder (LW-127): the CT+Speed clock-model ranking the phase-0
/// probe validated 15/15 across two sessions and a restart (tools/probes/provoke_lookahead_probe.py's
/// project(), not its leaders() -- the two diverge whenever two min-ETA candidates carry different
/// Speeds; T8 below pins the counterexample), the candidate filter, and TryEta's standalone read.
/// Module-level Provoke-hold wiring (the hide/reveal decision itself) lives in ProvokeHoldTests.cs.
/// </summary>
public class TurnOrderTests
{
    /// <summary>TurnOrder is stateless by contract (the adversarial review's NIT); every call site
    /// owns its own scratch buffer, mirroring how ProvokeHold owns `_hideScratch`. One shared
    /// instance-per-test-class buffer is fine here -- xUnit constructs a fresh TurnOrderTests
    /// instance per [Fact], so there is no cross-test state leak.</summary>
    private readonly List<(long e, int ct, int speed)> _scratch = new();

    private static void SeatEnemyCandidate(FakeSparseMemory m, int bandIdx, int ct, int speed,
        int lvl = 20, int br = 30, int fa = 30, int gx = 5, int gy = 5, int hp = 100, int maxHp = 100)
    {
        MemSeats.SeatBand(m, bandIdx, weapon: 0, lvl, br, fa, gx, gy, hp: hp, maxHp: maxHp,
            ctSlam: ct, speed: speed);
        m.U8s[Band.Entry(bandIdx) + Offsets.AFriendFoe] = Offsets.AFriendFoeEnemyBit;
    }

    // ---- T8: the load-bearing ranking correction (B1) ----

    [Fact]
    public void TryNextEnemyToAct_picks_the_CT_overshoot_winner_not_the_lower_ETA_seat_T8()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 80, speed: 10);   // "A": eta 2, lands exactly on 100 after the wait
        SeatEnemyCandidate(m, 1, ct: 70, speed: 20);   // "B": eta 2 too, but overshoots to 110

        Assert.True(TurnOrder.TryNextEnemyToAct(m, _scratch, out long entry, out int leaderEta));
        Assert.Equal(Band.Entry(1), entry);   // B, not A -- leaders() (the WRONG function) would pick A
        Assert.Equal(2, leaderEta);
    }

    // ---- T8b: the hp/maxHp rejection the probe learned the hard way live ----

    [Fact]
    public void TryNextEnemyToAct_excludes_a_seat_whose_hp_exceeds_its_maxHp_T8b()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 99, speed: 999, hp: 437, maxHp: 96);   // the exact live garbage read
        SeatEnemyCandidate(m, 1, ct: 10, speed: 5);

        Assert.True(TurnOrder.TryNextEnemyToAct(m, _scratch, out long entry, out _));
        Assert.Equal(Band.Entry(1), entry);   // the garbage seat never wins, or even competes
    }

    [Fact]
    public void TryNextEnemyToAct_false_when_no_enemy_candidate_has_a_nonzero_speed()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 50, speed: 0);

        Assert.False(TurnOrder.TryNextEnemyToAct(m, _scratch, out _, out _));
    }

    [Fact]
    public void TryNextEnemyToAct_ignores_player_side_seats_even_at_a_pinned_CT_100()
    {
        var m = new FakeSparseMemory();
        // A player-side seat pinned exactly the way ExtraTurn's own live slam does (Offsets.ACtSlam
        // write target, band +0x25), plus a high Speed -- if this ever leaked into the ranking it
        // would win every tick outright (CT >= 100 -> eta 0).
        MemSeats.SeatBand(m, 0, weapon: 5, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2, ctSlam: 100, speed: 99);
        SeatEnemyCandidate(m, 1, ct: 10, speed: 5);

        Assert.True(TurnOrder.TryNextEnemyToAct(m, _scratch, out long entry, out _));
        Assert.Equal(Band.Entry(1), entry);   // the only ENEMY candidate wins; the player was never in the race
    }

    [Fact]
    public void TryNextEnemyToAct_ignores_an_offfield_enemy_seat()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 99, speed: 50);
        m.U8s[Band.Entry(0) + Offsets.AGateByte] = Offsets.AGateHiddenValue;   // ghost seat
        SeatEnemyCandidate(m, 1, ct: 10, speed: 5);

        Assert.True(TurnOrder.TryNextEnemyToAct(m, _scratch, out long entry, out _));
        Assert.Equal(Band.Entry(1), entry);
    }

    [Fact]
    public void TryNextEnemyToAct_ignores_a_structurally_dead_enemy_seat()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 99, speed: 50);
        m.U8s[Band.Entry(0) + Offsets.ADeadStatus] = Offsets.ADeadBit;
        SeatEnemyCandidate(m, 1, ct: 10, speed: 5);

        Assert.True(TurnOrder.TryNextEnemyToAct(m, _scratch, out long entry, out _));
        Assert.Equal(Band.Entry(1), entry);
    }

    // ---- TryEta ----

    [Fact]
    public void TryEta_reads_the_seats_own_raw_eta()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 70, speed: 20);

        Assert.True(TurnOrder.TryEta(m, Band.Entry(0), out int eta));
        Assert.Equal(2, eta);   // ceil((100-70)/20) = ceil(1.5) = 2
    }

    [Fact]
    public void TryEta_zero_once_CT_has_reached_or_overshot_the_threshold()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 110, speed: 20);

        Assert.True(TurnOrder.TryEta(m, Band.Entry(0), out int eta));
        Assert.Equal(0, eta);
    }

    [Fact]
    public void TryEta_false_when_speed_is_zero()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 50, speed: 0);

        Assert.False(TurnOrder.TryEta(m, Band.Entry(0), out _));
    }

    // ---- BLOCKER-1 (adversarial review): TryEta must run the SAME candidate gate the ranking
    // does, not just Speed > 0. A caller's already-located entry (LocateByIdentity's contract)
    // guarantees only Band.IsValid -- nothing about on-field, enemy-side, or hp<=maxHp. ----

    [Fact]
    public void TryEta_false_for_an_off_field_seat_even_with_a_healthy_speed()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 50, speed: 10);
        m.U8s[Band.Entry(0) + Offsets.AGateByte] = Offsets.AGateHiddenValue;

        Assert.False(TurnOrder.TryEta(m, Band.Entry(0), out _));
    }

    [Fact]
    public void TryEta_false_for_a_seat_whose_hp_exceeds_its_maxHp()
    {
        var m = new FakeSparseMemory();
        SeatEnemyCandidate(m, 0, ct: 50, speed: 10, hp: 9999, maxHp: 100);

        Assert.False(TurnOrder.TryEta(m, Band.Entry(0), out _));
    }

    [Fact]
    public void TryEta_false_for_a_player_side_seat_even_at_a_pinned_CT_100()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 0, weapon: 5, lvl: 25, br: 40, fa: 60, gx: 2, gy: 2, ctSlam: 100, speed: 99);
        // no AFriendFoe bit set -> reads player-side by default

        Assert.False(TurnOrder.TryEta(m, Band.Entry(0), out _));
    }
}
