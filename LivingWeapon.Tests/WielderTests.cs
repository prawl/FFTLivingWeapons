using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Wielder.TryResolve + Locate + LocateAll. Twin filter: real-position beats frozen (0,0);
/// identical-twin tie (unit on corner tile) returns one; genuine ambiguity returns 0.
/// </summary>
public class WielderTests
{
    private const int Weapon = 56;   // any catalogued id; tests use the Umbral Rod's

    // ---- TryResolve ----

    [Fact]
    public void TryResolve_false_when_nobody_wields_the_weapon()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: 1);
        Assert.False(Wielder.TryResolve(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolve_finds_the_single_wielder_fingerprint_and_hands()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: Weapon);
        var hands = new List<int>();
        Assert.True(Wielder.TryResolve(m, Weapon, out var fp, hands));
        Assert.Equal((31, 65, 58), fp);
        Assert.Contains(Weapon, hands); Assert.DoesNotContain(0xFFFF, hands);
    }

    [Fact]
    public void TryResolve_false_when_two_units_wield_it()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        Assert.False(Wielder.TryResolve(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolve_skips_empty_roster_slots()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatRoster(m, 0, lvl: 0, br: 70, fa: 50, rh: Weapon);
        Assert.False(Wielder.TryResolve(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolve_finds_an_offhand_wielder()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatRoster(m, 3, lvl: 40, br: 80, fa: 45, rh: 1, oh: Weapon);
        var hands = new List<int>();
        Assert.True(Wielder.TryResolve(m, Weapon, out var fp, hands));
        Assert.Equal((40, 80, 45), fp); Assert.Contains(Weapon, hands); Assert.Contains(1, hands);
    }

    // ---- Locate ----

    [Fact]
    public void Locate_finds_the_band_entry_by_weapon_and_fingerprint()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        Assert.Equal(Band.Entry(5), Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_prefers_the_real_position_over_the_frozen_origin_twin()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 3, Weapon, lvl: 31, br: 65, fa: 58, gx: 0, gy: 0);
        MemSeats.SeatBand(m, 9, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);
        Assert.Equal(Band.Entry(9), Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_returns_zero_on_a_surviving_tie()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 3, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 4);
        MemSeats.SeatBand(m, 9, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);
        Assert.Equal(0, Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_rejects_a_fingerprint_mismatch()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 66, fa: 58, gx: 4, gy: 7);
        Assert.Equal(0, Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_matches_the_other_hand_when_the_weapon_rides_offhand()
    {
        var m = new FakeSparseMemory(); MemSeats.SeatBand(m, 7, 1, lvl: 40, br: 80, fa: 45, gx: 3, gy: 3);
        Assert.Equal(Band.Entry(7), Wielder.Locate(m, Weapon, new[] { 1, Weapon }, (40, 80, 45)));
    }

    // ---- Locate: identical-twin tie-break (corner-tile (0,0) fix) ----

    [Fact]
    public void Locate_returns_one_when_two_candidates_are_identical_twins_both_at_origin()
    {
        // Same identity tuple, both at (0,0): unit on the corner tile -- return one, not 0.
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 25, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        MemSeats.SeatBand(m, 28, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (99, 89, 76));
        Assert.NotEqual(0, e);
        Assert.True(e == Band.Entry(25) || e == Band.Entry(28));
    }

    [Fact]
    public void Locate_twin_tie_does_not_fire_when_entries_carry_different_weapon_ids()
    {
        // Dual-wield: exact-match rank resolves before the twin-tie path fires.
        const int Other = Weapon + 1;
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 25, Other,  lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        MemSeats.SeatBand(m, 28, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon, Other }, (99, 89, 76));
        Assert.Equal(Band.Entry(28), e);
    }

    [Fact]
    public void Locate_twin_tie_prefers_real_position_candidate_when_one_is_off_origin()
    {
        // One twin at (0,0), one at a real position: real-position wins (existing behavior).
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 25, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        MemSeats.SeatBand(m, 28, Weapon, lvl: 99, br: 89, fa: 76, gx: 3, gy: 5);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (99, 89, 76));
        Assert.Equal(Band.Entry(28), e);
    }

    // ---- LocateAll ----
    [Fact]
    public void LocateAll_returns_both_twins_regardless_of_position()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 25, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        MemSeats.SeatBand(m, 28, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (99, 89, 76), results);
        Assert.Equal(2, results.Count);
        Assert.Contains(Band.Entry(25), results);
        Assert.Contains(Band.Entry(28), results);
    }

    [Fact]
    public void LocateAll_returns_single_entry_when_no_twin()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (31, 65, 58), results);
        Assert.Single(results);
        Assert.Equal(Band.Entry(5), results[0]);
    }

    [Fact]
    public void LocateAll_returns_empty_when_no_match()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 66, fa: 58, gx: 4, gy: 7);   // brave mismatch
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (31, 65, 58), results);
        Assert.Empty(results);
    }

    // ---- Enemy level collision guard ----
    // An enemy sharing weapon + brave + faith but a DIFFERENT level must not be adopted.
    // These tests will fail until Locate/LocateAll compare fp.lvl.

    [Fact]
    public void Locate_rejects_enemy_with_same_weapon_brave_faith_but_different_level()
    {
        var m = new FakeSparseMemory();
        // Player fp: lvl=30, br=65, fa=58 -- enemy in the band has same weapon/brave/faith but lvl=25.
        MemSeats.SeatBand(m, 10, Weapon, lvl: 25, br: 65, fa: 58, gx: 5, gy: 3);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (30, 65, 58));
        Assert.Equal(0, e);
    }

    [Fact]
    public void Locate_returns_entry_when_level_also_matches()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 10, Weapon, lvl: 30, br: 65, fa: 58, gx: 5, gy: 3);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (30, 65, 58));
        Assert.Equal(Band.Entry(10), e);
    }

    [Fact]
    public void LocateAll_rejects_entry_with_same_weapon_brave_faith_but_different_level()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 10, Weapon, lvl: 25, br: 65, fa: 58, gx: 5, gy: 3);   // level mismatch
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (30, 65, 58), results);
        Assert.Empty(results);
    }

    [Fact]
    public void LocateAll_accepts_entry_when_level_also_matches()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatBand(m, 10, Weapon, lvl: 30, br: 65, fa: 58, gx: 5, gy: 3);
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (30, 65, 58), results);
        Assert.Single(results);
    }

    // ---- TryResolveMainHand: main-hand-only resolution ----

    [Fact]
    public void TryResolveMainHand_true_when_weapon_is_in_RRHand()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: Weapon);
        var hands = new List<int>();
        Assert.True(Wielder.TryResolveMainHand(m, Weapon, out var fp, hands));
        Assert.Equal((31, 65, 58), fp);
        Assert.Equal(new List<int> { Weapon }, hands);
    }

    [Fact]
    public void TryResolveMainHand_false_when_weapon_is_only_in_offhand()
    {
        // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: 1, oh: Weapon);
        Assert.False(Wielder.TryResolveMainHand(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolveMainHand_hands_list_contains_only_the_main_hand_id()
    {
        // The locate set for a main-hand Zwill must only pass the main-hand id so that the
        // band locate keyed on the main-hand id is an exact field match.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon, oh: 99);
        var hands = new List<int>();
        Assert.True(Wielder.TryResolveMainHand(m, Weapon, out _, hands));
        Assert.Equal(new List<int> { Weapon }, hands);
        Assert.DoesNotContain(99, hands);
    }

    [Fact]
    public void TryResolveMainHand_false_when_nobody_wields_it()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: 1);
        Assert.False(Wielder.TryResolveMainHand(m, Weapon, out _, new List<int>()));
    }

    [Fact]
    public void TryResolveMainHand_false_when_two_roster_slots_have_it_as_main_hand()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        Assert.False(Wielder.TryResolveMainHand(m, Weapon, out _, new List<int>()));
    }

    // ---- ResolveDeployedMainHand: a benched reserve duplicate no longer freezes the steal ----

    [Fact]
    public void ResolveDeployedMainHand_returns_the_deployed_wielder_ignoring_a_reserve_duplicate()
    {
        // Two roster slots hold the weapon in the main hand, but only slot 0 is DEPLOYED (has a band
        // entry). Slot 3 is a benched reserve (no band entry). The reserve must NOT create ambiguity
        // -- the exact bug that froze Larceny when the dev give-all armed a reserve with Arcanum.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 99, br: 97, fa: 75, rh: Weapon);
        MemSeats.SeatRoster(m, 3, lvl: 99, br: 89, fa: 76, rh: Weapon);   // reserve: no band entry
        MemSeats.SeatBand(m, 12, Weapon, lvl: 99, br: 97, fa: 75, gx: 3, gy: 5);
        Assert.Equal(Band.Entry(12), Wielder.ResolveDeployedMainHand(m, Weapon, out var fp));
        Assert.Equal((99, 97, 75), fp);
    }

    [Fact]
    public void ResolveDeployedMainHand_returns_the_single_deployed_wielder()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: Weapon);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        Assert.Equal(Band.Entry(5), Wielder.ResolveDeployedMainHand(m, Weapon, out var fp));
        Assert.Equal((31, 65, 58), fp);
    }

    [Fact]
    public void ResolveDeployedMainHand_zero_when_the_wielder_is_in_the_roster_but_not_deployed()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);   // no band entry
        Assert.Equal(0, Wielder.ResolveDeployedMainHand(m, Weapon, out _));
    }

    [Fact]
    public void ResolveDeployedMainHand_zero_when_two_wielders_are_both_deployed()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        MemSeats.SeatBand(m, 4, Weapon, lvl: 20, br: 70, fa: 50, gx: 2, gy: 2);   // both on the field
        MemSeats.SeatBand(m, 8, Weapon, lvl: 25, br: 60, fa: 40, gx: 6, gy: 6);
        Assert.Equal(0, Wielder.ResolveDeployedMainHand(m, Weapon, out _));   // genuinely ambiguous
    }

    [Fact]
    public void ResolveDeployedMainHand_ignores_an_offhand_only_wielder()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: 1, oh: Weapon);   // weapon in the OFF hand
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        Assert.Equal(0, Wielder.ResolveDeployedMainHand(m, Weapon, out _));   // main-hand only
    }

}
