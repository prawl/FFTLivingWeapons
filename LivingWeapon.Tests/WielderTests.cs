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
        // Why this still bails post-nameId-locate (do not "fix" it): no roster slot is seeded
        // here, so Wielder.ResolveAnyHandNameId resolves -1 (zero matching roster slots) and
        // Locate's rosterNameId parameter comes in <= 0 -- tier 1 never runs, and tier 2's veto
        // is inert at rosterNameId <= 0, so this is byte-for-byte the pre-nameId fp tie-break bail.
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

    // ---- Locate: nameId-primary two-tier locate (Plan v2 D1-D4) ----
    // Roster nameId 298 identifies the real wielder; a band-only "enemy" sharing weapon+fp but
    // carrying a DIFFERENT frame nameId (918) is the fp-collider the fingerprint-only algorithm
    // used to hand out by accident.

    [Fact]
    public void Locate_prefers_the_nameId_verified_wielder_over_an_fp_colliding_enemy()
    {
        // [LOAD-BEARING, TDD item 1a] Both the real wielder (nameId 298, matching the roster)
        // and an enemy fp-collider (nameId 918) are present -- tier 1 only ever sees the wielder
        // as a candidate (the collider's nameId never equals rosterNameId), so it resolves
        // cleanly instead of the old fp-only algorithm's tie-bail.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        MemSeats.SeatFrameNameId(m, 5, 298);
        MemSeats.SeatBand(m, 10, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);   // enemy fp-collider
        MemSeats.SeatFrameNameId(m, 10, 918);
        Assert.Equal(Band.Entry(5), Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_returns_zero_when_only_the_fp_colliding_enemy_is_deployed()
    {
        // [LOAD-BEARING, TDD item 1b] The wielder never got a band entry this battle; only the
        // enemy fp-collider exists. The old fp-only algorithm handed out the ENEMY's address here
        // (the wrong-unit write hazard) -- the tier-2 veto now excludes a foreign nonzero nameId
        // even when it is the only fp match left, so Locate returns 0 instead.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 10, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);   // enemy fp-collider only
        MemSeats.SeatFrameNameId(m, 10, 918);
        Assert.Equal(0, Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_returns_a_deterministic_pick_when_two_real_position_entries_share_the_nameId()
    {
        // [TDD item 2, the Iai-starvation generalization] A revolving mirror clone can carry a
        // REAL grid position, unlike the frozen-(0,0) twin the old tie-break already handled.
        // Two real-position entries verified by the SAME frame nameId are known copies of ONE
        // identified unit -- tier 1 now returns a deterministic pick instead of bailing.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 99, br: 89, fa: 76, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 20, Weapon, lvl: 99, br: 89, fa: 76, gx: 9, gy: 9);
        MemSeats.SeatFrameNameId(m, 20, 298);
        MemSeats.SeatBand(m, 24, Weapon, lvl: 99, br: 89, fa: 76, gx: 2, gy: 2);
        MemSeats.SeatFrameNameId(m, 24, 298);
        long e = Wielder.Locate(m, Weapon, new[] { Weapon }, (99, 89, 76));
        Assert.NotEqual(0, e);
        Assert.True(e == Band.Entry(20) || e == Band.Entry(24));
    }

    [Fact]
    public void Locate_rejects_a_nameId_match_whose_brave_faith_dont_match_the_fingerprint()
    {
        // [TDD item 3, pool-overlap guard] A nameId match alone is not enough -- D1's stat
        // cross-check exists because the enemy/player nameId pool overlap is unproven. An entry
        // carrying the SAME nameId but the WRONG brave/faith is not a tier-1 candidate (and fails
        // the identical base predicate in tier 2 too, so this is a flat reject, not a fallback).
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 70, fa: 40, gx: 4, gy: 7);   // wrong brave/faith
        MemSeats.SeatFrameNameId(m, 5, 298);
        Assert.Equal(0, Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_roster_nameId_zero_is_not_a_zero_equals_zero_trap()
    {
        // [TDD item 4, the 0-trap] Roster nameId unseeded (0) must be treated as "capture failed",
        // NOT as a valid identity of 0 -- so an entry whose OWN frame nameId also reads 0 (equally
        // unseeded) must not be accepted via a 0==0 tier-1 match. rosterNameId <= 0 skips tier 1
        // entirely; this locates via tier 2's ordinary fp match (veto inert), same as before.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon);            // nameId defaults 0
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);    // frame nameId also unseeded (0)
        Assert.Equal(Band.Entry(5), Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void Locate_falls_back_to_tier_two_when_no_entry_carries_the_roster_nameId()
    {
        // [TDD item 5, tier-2 trigger] The roster nameId capture succeeds (298), but no band
        // entry's own frame nameId was ever seeded (reads 0) -- tier 1 finds zero candidates, so
        // Locate falls to tier 2, whose veto explicitly passes a nameId-0 entry.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);   // frame nameId unseeded (0)
        Assert.Equal(Band.Entry(5), Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void ResolveAnyHandNameId_returns_minus_one_when_two_roster_slots_match_with_distinct_nameIds()
    {
        // [TDD item 6(i)] Two roster slots both hold the weapon (one main hand, one off hand)
        // with the SAME fp but DISTINCT nameIds -- ambiguous capture, -1 (routes Locate to tier 2).
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 100);
        MemSeats.SeatRoster(m, 1, lvl: 31, br: 65, fa: 58, rh: 1, oh: Weapon, nameId: 200);
        Assert.Equal(-1, Wielder.ResolveAnyHandNameId(m, Weapon, (31, 65, 58)));
    }

    [Fact]
    public void ResolveAnyHandNameId_returns_minus_one_when_two_roster_slots_match_with_the_same_nameId()
    {
        // [TDD item 6(ii), B2 pin] Unlike RosterNameId (main-hand, distinct-nameId contract that
        // Iai depends on unchanged), this any-hand sibling refuses on ANY multi-slot match -- even
        // when both slots happen to carry nameId 100 -- because a locate write must never pick
        // between two units.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 100);
        MemSeats.SeatRoster(m, 1, lvl: 31, br: 65, fa: 58, rh: 1, oh: Weapon, nameId: 100);
        Assert.Equal(-1, Wielder.ResolveAnyHandNameId(m, Weapon, (31, 65, 58)));
    }

    [Fact]
    public void Locate_tier_two_veto_prefers_the_nameId_zero_entry_over_a_foreign_nameId_collider()
    {
        // [TDD item 7, B1 pin] Tier 1 is empty (neither entry carries nameId 298). Tier 2 must
        // discriminate: the nameId-0 (unseeded) entry passes the veto, the foreign-nameId (401)
        // collider is excluded -- so Locate returns the nameId-0 entry, never the collider.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);    // nameId unseeded (0)
        MemSeats.SeatBand(m, 10, Weapon, lvl: 31, br: 65, fa: 58, gx: 6, gy: 2);   // foreign nameId
        MemSeats.SeatFrameNameId(m, 10, 401);
        Assert.Equal(Band.Entry(5), Wielder.Locate(m, Weapon, new[] { Weapon }, (31, 65, 58)));
    }

    [Fact]
    public void LocateAll_tier_one_collects_only_nameId_matching_copies()
    {
        // [TDD item 8a] Both twins carry the roster-matching nameId -- tier 1 collects both,
        // same as the plain fp-based LocateAll twin behavior (LocateAll_returns_both_twins_...).
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 99, br: 89, fa: 76, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 25, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        MemSeats.SeatFrameNameId(m, 25, 298);
        MemSeats.SeatBand(m, 28, Weapon, lvl: 99, br: 89, fa: 76, gx: 0, gy: 0);
        MemSeats.SeatFrameNameId(m, 28, 298);
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (99, 89, 76), results);
        Assert.Equal(2, results.Count);
        Assert.Contains(Band.Entry(25), results);
        Assert.Contains(Band.Entry(28), results);
    }

    [Fact]
    public void LocateAll_veto_leaves_results_empty_when_only_the_foreign_nameId_collider_is_present()
    {
        // [TDD item 8b] SpiritualFont writes MP through LocateAll -- with only the collider
        // present, the veto must leave results EMPTY rather than handing out the enemy's entry.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 99, br: 89, fa: 76, rh: Weapon, nameId: 298);
        MemSeats.SeatBand(m, 10, Weapon, lvl: 99, br: 89, fa: 76, gx: 6, gy: 2);
        MemSeats.SeatFrameNameId(m, 10, 918);
        var results = new List<long>();
        Wielder.LocateAll(m, Weapon, new[] { Weapon }, (99, 89, 76), results);
        Assert.Empty(results);
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

    // ---- AnyDeployedMainHand: "is this weapon even in play?" (gates unequipped gate-log spam) ----

    [Fact]
    public void AnyDeployedMainHand_false_when_nobody_holds_it()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: 1);
        Assert.False(Wielder.AnyDeployedMainHand(m, Weapon));
    }

    [Fact]
    public void AnyDeployedMainHand_true_when_a_deployed_unit_holds_it_mainhand()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: Weapon);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        Assert.True(Wielder.AnyDeployedMainHand(m, Weapon));
    }

    [Fact]
    public void AnyDeployedMainHand_false_when_held_but_benched()
    {
        // Roster main-hand wielder with NO band entry -- equipped in the menu but not in this
        // battle. This is the exact case that spammed the gate log: a seeded/give-all reserve.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);   // no band entry
        Assert.False(Wielder.AnyDeployedMainHand(m, Weapon));
    }

    [Fact]
    public void AnyDeployedMainHand_false_when_only_offhand()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: 1, oh: Weapon);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        Assert.False(Wielder.AnyDeployedMainHand(m, Weapon));   // main-hand only
    }

    [Fact]
    public void AnyDeployedMainHand_true_with_two_deployed_wielders()
    {
        // Unlike ResolveDeployedMainHand it does NOT bail on ambiguity -- the question is only
        // "is the weapon in play", so two deployed wielders still answer true.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        MemSeats.SeatBand(m, 4, Weapon, lvl: 20, br: 70, fa: 50, gx: 2, gy: 2);
        MemSeats.SeatBand(m, 8, Weapon, lvl: 25, br: 60, fa: 40, gx: 6, gy: 6);
        Assert.True(Wielder.AnyDeployedMainHand(m, Weapon));
    }

    // ---- ResolveDeployedMainHandAll ----

    [Fact]
    public void ResolveDeployedMainHandAll_empty_when_nobody_holds_it()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: 1);
        var results = new List<(long, (int, int, int))>();
        Wielder.ResolveDeployedMainHandAll(m, Weapon, results);
        Assert.Empty(results);
    }

    [Fact]
    public void ResolveDeployedMainHandAll_one_result_for_single_deployed_wielder()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 2, lvl: 31, br: 65, fa: 58, rh: Weapon);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        var results = new List<(long, (int, int, int))>();
        Wielder.ResolveDeployedMainHandAll(m, Weapon, results);
        Assert.Single(results);
        Assert.Equal(Band.Entry(5), results[0].Item1);
        Assert.Equal((31, 65, 58), results[0].Item2);
    }

    [Fact]
    public void ResolveDeployedMainHandAll_two_results_for_two_deployed_wielders()
    {
        // Key invariant: does NOT bail when two roster slots are both deployed -- returns both.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        MemSeats.SeatBand(m, 4, Weapon, lvl: 20, br: 70, fa: 50, gx: 2, gy: 2);
        MemSeats.SeatBand(m, 8, Weapon, lvl: 25, br: 60, fa: 40, gx: 6, gy: 6);
        var results = new List<(long, (int, int, int))>();
        Wielder.ResolveDeployedMainHandAll(m, Weapon, results);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Item1 == Band.Entry(4));
        Assert.Contains(results, r => r.Item1 == Band.Entry(8));
    }

    [Fact]
    public void ResolveDeployedMainHandAll_skips_benched_roster_slot()
    {
        // Roster slot 0 deployed (has band entry), slot 1 benched (no band entry) -> only slot 0 returned.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 99, br: 97, fa: 75, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 99, br: 89, fa: 76, rh: Weapon);   // benched
        MemSeats.SeatBand(m, 12, Weapon, lvl: 99, br: 97, fa: 75, gx: 3, gy: 5);
        var results = new List<(long, (int, int, int))>();
        Wielder.ResolveDeployedMainHandAll(m, Weapon, results);
        Assert.Single(results);
        Assert.Equal(Band.Entry(12), results[0].Item1);
    }

    [Fact]
    public void ResolveDeployedMainHandAll_clears_results_before_populating()
    {
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 31, br: 65, fa: 58, rh: Weapon);
        MemSeats.SeatBand(m, 5, Weapon, lvl: 31, br: 65, fa: 58, gx: 4, gy: 7);
        var results = new List<(long, (int, int, int))>();
        results.Add((9999L, (0, 0, 0)));   // stale entry
        Wielder.ResolveDeployedMainHandAll(m, Weapon, results);
        Assert.Single(results);
        Assert.Equal(Band.Entry(5), results[0].Item1);
    }

    [Fact]
    public void ResolveDeployedMainHand_still_zero_when_two_wielders_are_both_deployed_after_refactor()
    {
        // Regression: ResolveDeployedMainHand's existing single-bearer contract must hold after
        // the refactor to delegate to ResolveDeployedMainHandAll.
        var m = new FakeSparseMemory();
        MemSeats.SeatRoster(m, 0, lvl: 20, br: 70, fa: 50, rh: Weapon);
        MemSeats.SeatRoster(m, 1, lvl: 25, br: 60, fa: 40, rh: Weapon);
        MemSeats.SeatBand(m, 4, Weapon, lvl: 20, br: 70, fa: 50, gx: 2, gy: 2);
        MemSeats.SeatBand(m, 8, Weapon, lvl: 25, br: 60, fa: 40, gx: 6, gy: 6);
        Assert.Equal(0, Wielder.ResolveDeployedMainHand(m, Weapon, out _));
    }

}
