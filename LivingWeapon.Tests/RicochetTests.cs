using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Stormarc's "Chain Lightning" signature. While a +3 Stormarc is equipped and the wielder's
/// action deals damage to an ENEMY, the nearest OTHER live enemy within 3 Manhattan-distance
/// tiles takes chip damage equal to 50% of the original (floor 1, min HP 1 -- never kills).
///
/// Pure jobs in Ricochet.Policy.cs:
///   (1) IsActive: gates on ricochetRadius > 0 AND tier >= AtTier.
///   (2) ChipDamage: 50 pct integer floor, minimum 1 when original >= 1.
///   (3) PickTarget: nearest OTHER live ENEMY within radius; tie-break by band slot.
///   (4) ClampHp: chip damage is applied such that HP never falls below 1.
///   (5) Manhattan distance helper.
///   (6) RicochetState: HP-diff event detection. Consume() marks our own chip write as
///       already-seen so the bounce NEVER feeds another bounce (no chains).
///
/// Stateful runtime in Ricochet.cs: two-pass per tick -- pass 1 observes every valid band
/// slot (full candidate list, so higher band slots are reachable targets) and tags each
/// with enemy-side membership (static-array fingerprints, the EagleEye filter); pass 2
/// bounces each enemy damage event off the FULL list and consumes its own write.
/// </summary>
public class RicochetTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    // ---- (1) IsActive ----

    private static WeaponSignature RicochetSig(int radius = 3, int pct = 50, int atTier = 3) =>
        new() { AtTier = atTier, RicochetRadius = radius, RicochetPct = pct, DisplayLabel = "Chain Lightning" };

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Ricochet.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_radius_is_zero()
        => Assert.False(Ricochet.IsActive(new WeaponSignature { RicochetRadius = 0, RicochetPct = 50, AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Ricochet.IsActive(RicochetSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Ricochet.IsActive(RicochetSig(atTier: 3), tier: 3));
        Assert.True(Ricochet.IsActive(RicochetSig(atTier: 3), tier: 4));
    }

    // ---- (2) ChipDamage ----

    [Theory]
    [InlineData(10, 50, 5)]    // basic 50%
    [InlineData(11, 50, 5)]    // floor: 11*50/100 = 5
    [InlineData(1,  50, 1)]    // minimum 1
    [InlineData(2,  50, 1)]    // 2*50/100 = 1
    [InlineData(0,  50, 0)]    // 0 damage => 0 chip (no event)
    [InlineData(100, 50, 50)]
    [InlineData(200, 50, 100)]
    public void ChipDamage_is_floor_pct_minimum_1_when_nonzero(int dmg, int pct, int expected)
        => Assert.Equal(expected, Ricochet.ChipDamage(dmg, pct));

    // ---- (3) PickTarget: nearest other live ENEMY within radius ----

    private static Ricochet.SlotInfo S(int slot, int gx, int gy, int hp = 100, bool enemy = true)
        => new(slot, gx, gy, hp, enemy);

    [Fact]
    public void PickTarget_returns_minus1_when_no_candidates()
    {
        Assert.Equal(-1, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5,
                                              radius: 3, slots: Array.Empty<Ricochet.SlotInfo>()));
    }

    [Fact]
    public void PickTarget_excludes_the_victim_slot()
    {
        var slots = new[] { S(0, 5, 5) };
        Assert.Equal(-1, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_excludes_non_enemy_slots()
    {
        // A PLAYER ally standing right next to the victim must never be chipped.
        var slots = new[] { S(1, 6, 5, enemy: false), S(2, 5, 8) };   // ally adjacent, enemy at distance 3
        Assert.Equal(2, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_returns_minus1_when_only_allies_in_radius()
    {
        var slots = new[] { S(1, 6, 5, enemy: false) };
        Assert.Equal(-1, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_excludes_slots_outside_radius()
    {
        var slots = new[] { S(1, 9, 5) };   // Manhattan 4 from (5,5)
        Assert.Equal(-1, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_excludes_dead_slots()
    {
        var slots = new[] { S(1, 6, 5, hp: 0) };
        Assert.Equal(-1, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_picks_closest_by_manhattan()
    {
        var slots = new[] { S(2, 7, 5), S(3, 6, 5) };
        Assert.Equal(3, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_finds_higher_band_slots_than_the_victim()
    {
        // Regression: the target list must be complete BEFORE events are processed --
        // a victim in slot 5 must be able to bounce to slot 9.
        var slots = new[] { S(5, 5, 5), S(9, 6, 5) };
        Assert.Equal(9, Ricochet.PickTarget(victimSlot: 5, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_tiebreaks_by_lower_slot()
    {
        var slots = new[] { S(5, 6, 5), S(2, 5, 6) };
        Assert.Equal(2, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    [Fact]
    public void PickTarget_accepts_boundary_distance_equal_to_radius()
    {
        var slots = new[] { S(1, 8, 5) };
        Assert.Equal(1, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }

    // ---- (4) ClampHp: chip never kills ----

    [Theory]
    [InlineData(100, 50, 50)]   // normal
    [InlineData(10,  10,  1)]   // chip would exactly kill -> clamp to 1
    [InlineData(5,   10,  1)]   // chip exceeds hp -> clamp to 1
    [InlineData(2,    1,  1)]   // chip 1 on hp 2 -> hp 1 (fine)
    [InlineData(1,    1,  1)]   // already at 1 -> stays 1
    public void ClampHp_never_goes_below_1(int currentHp, int chip, int expectedNewHp)
        => Assert.Equal(expectedNewHp, Ricochet.ClampHp(currentHp, chip));

    // ---- (5) Manhattan distance ----

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(3, 4, 0, 0, 7)]
    [InlineData(5, 5, 8, 5, 3)]
    [InlineData(5, 5, 5, 8, 3)]
    [InlineData(5, 5, 7, 7, 4)]
    public void Manhattan_is_abs_dx_plus_abs_dy(int x1, int y1, int x2, int y2, int expected)
        => Assert.Equal(expected, Ricochet.Manhattan(x1, y1, x2, y2));

    // ---- (6) RicochetState: HP-diff events + no-chain consume ----

    [Fact]
    public void Observe_baselines_first_sighting_silently()
    {
        var state = new RicochetState(Offsets.BandSlots);
        Assert.Equal(0, state.Observe(slot: 0, currentHp: 100));
    }

    [Fact]
    public void Observe_reports_a_drop_once_then_goes_quiet()
    {
        var state = new RicochetState(Offsets.BandSlots);
        state.Observe(0, 100);                       // baseline
        Assert.Equal(40, state.Observe(0, 60));      // the damage event
        Assert.Equal(0, state.Observe(0, 60));       // same hp re-read: not an event
    }

    [Fact]
    public void Observe_reports_a_second_distinct_drop()
    {
        var state = new RicochetState(Offsets.BandSlots);
        state.Observe(0, 100);
        Assert.Equal(40, state.Observe(0, 60));
        Assert.Equal(20, state.Observe(0, 40));
    }

    [Fact]
    public void Observe_ignores_heals()
    {
        var state = new RicochetState(Offsets.BandSlots);
        state.Observe(0, 60);
        Assert.Equal(0, state.Observe(0, 100));      // HP up = heal/revive, not damage
    }

    [Fact]
    public void Consume_prevents_our_own_chip_from_chaining()
    {
        // Regression for the chain bug: we chip slot 3 from 100 -> 90 ourselves; without
        // Consume the next tick would read 90, see a -10 "event", and bounce again.
        var state = new RicochetState(Offsets.BandSlots);
        state.Observe(3, 100);                       // pass-1 baseline (pre-chip)
        state.Consume(3, 90);                        // pass-2: we wrote 90
        Assert.Equal(0, state.Observe(3, 90));       // our write is NOT a damage event
    }

    [Fact]
    public void Consume_keeps_real_followup_damage_visible()
    {
        var state = new RicochetState(Offsets.BandSlots);
        state.Observe(3, 100);
        state.Consume(3, 90);
        Assert.Equal(30, state.Observe(3, 60));      // a real hit after our chip still fires
    }

    [Fact]
    public void ResetBattle_clears_baselines()
    {
        var state = new RicochetState(Offsets.BandSlots);
        state.Observe(0, 100);
        state.ResetBattle();
        Assert.Equal(0, state.Observe(0, 40));       // fresh battle: first sight baselines, no event
    }

    // ---- Guarded HP write (in-process buffer stands in for the band entry) ----

    private static PinnedBuf MakeUnit(int hp, int maxHp, int gx, int gy,
                                      int lvl = 20, int br = 50, int fa = 50)
    {
        var unit = PinnedBuf.Of(256);
        var buf = unit.Bytes;
        buf[Offsets.ALevel] = (byte)lvl;
        buf[Offsets.ABrave] = (byte)br;
        buf[Offsets.AFaith] = (byte)fa;
        buf[Offsets.AMaxHp]     = (byte)(maxHp & 0xFF);
        buf[Offsets.AMaxHp + 1] = (byte)((maxHp >> 8) & 0xFF);
        buf[Offsets.AHp]     = (byte)(hp & 0xFF);
        buf[Offsets.AHp + 1] = (byte)((hp >> 8) & 0xFF);
        buf[Offsets.AGx] = (byte)gx;
        buf[Offsets.AGy] = (byte)gy;
        return unit;
    }

    private static int ReadHp(byte[] buf) => buf[Offsets.AHp] | (buf[Offsets.AHp + 1] << 8);

    [Fact]
    public void ApplyChip_writes_clamped_hp_and_never_kills()
    {
        using var unit = MakeUnit(hp: 3, maxHp: 100, gx: 5, gy: 5);
        Ricochet.ApplyChip(Live, unit.Addr, currentHp: 3, chip: 10);
        Assert.Equal(1, ReadHp(unit.Bytes));
    }

    [Fact]
    public void ApplyChip_writes_normal_chip_when_target_survives()
    {
        using var unit = MakeUnit(hp: 50, maxHp: 100, gx: 5, gy: 5);
        Ricochet.ApplyChip(Live, unit.Addr, currentHp: 50, chip: 20);
        Assert.Equal(30, ReadHp(unit.Bytes));
    }

    [Fact]
    public void ApplyChip_is_no_op_when_target_already_at_1()
    {
        using var unit = MakeUnit(hp: 1, maxHp: 100, gx: 5, gy: 5);
        Ricochet.ApplyChip(Live, unit.Addr, currentHp: 1, chip: 5);
        Assert.Equal(1, ReadHp(unit.Bytes));
    }

    // ---- Main-hand-only activation gate (B1) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.

    [Fact]
    public void IsActingMainHand_true_when_mainHand_is_the_signature_weapon()
        => Assert.True(Signatures.IsActingMainHand(mainHand: 86, weaponId: 86));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_a_different_weapon()
        => Assert.False(Signatures.IsActingMainHand(mainHand: 99, weaponId: 86));

    [Fact]
    public void IsActingMainHand_false_when_mainHand_is_zero_meaning_no_actor_resolved()
        => Assert.False(Signatures.IsActingMainHand(mainHand: 0, weaponId: 86));

    // ---- PickChain: greedy nearest-unhit multi-hop chain ----

    /// <summary>Load-bearing re-centering test: slot1 is within radius of the victim (5,5),
    /// slot2 is out of radius from victim but within radius of slot1. Only a re-centering
    /// implementation reaches slot2.</summary>
    [Fact]
    public void PickChain_recenters_each_hop_reaching_beyond_victim_radius()
    {
        // victim at (5,5); slot1 at (5,8) = dist 3; slot2 at (5,11) = dist 6 from victim, dist 3 from slot1
        var slots = new[] { S(1, 5, 8), S(2, 5, 11) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(new[] { 1, 2 }, result);
    }

    [Fact]
    public void PickChain_single_hop_when_only_one_in_radius()
    {
        var slots = new[] { S(1, 6, 5) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(new[] { 1 }, result);
    }

    [Fact]
    public void PickChain_respects_maxHops_cap()
    {
        // line of enemies: slot1 at dist1, slot2 at dist2, slot3 at dist3, slot4 at dist4 (all reachable hopping)
        var slots = new[] { S(1, 6, 5), S(2, 7, 5), S(3, 8, 5), S(4, 9, 5) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void PickChain_stops_when_next_hop_out_of_radius()
    {
        // slot1 at dist3 from victim; slot2 at dist4 from slot1 (out of radius 3)
        var slots = new[] { S(1, 8, 5), S(2, 12, 5) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(new[] { 1 }, result);
    }

    [Fact]
    public void PickChain_never_targets_allies_or_dead()
    {
        var slots = new[] { S(1, 6, 5, enemy: false), S(2, 7, 5, hp: 0), S(3, 5, 7) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(new[] { 3 }, result);
    }

    [Fact]
    public void PickChain_skips_slots_in_the_excluded_set()
    {
        // slot1 would be nearest but is already struck this tick; slot2 is next
        var slots = new[] { S(1, 6, 5), S(2, 5, 6) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0, 1 });
        Assert.Equal(new[] { 2 }, result);
    }

    [Fact]
    public void PickChain_empty_when_no_candidates()
    {
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3,
                                        slots: System.Array.Empty<Ricochet.SlotInfo>(),
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Empty(result);
    }

    [Fact]
    public void PickChain_tiebreak_lower_slot_when_lower_iterated_first()
    {
        // Both at dist 1; lower slot (2) comes first in array -- must still win
        var slots = new[] { S(2, 6, 5), S(5, 5, 6) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(2, result[0]);
    }

    [Fact]
    public void PickChain_tiebreak_lower_slot_when_lower_iterated_last()
    {
        // Both at dist 1; lower slot (2) comes LAST in array -- must still win
        var slots = new[] { S(5, 6, 5), S(2, 5, 6) };
        var result = Ricochet.PickChain(startGx: 5, startGy: 5, radius: 3, maxHops: 3, slots: slots,
                                        excluded: new System.Collections.Generic.HashSet<int> { 0 });
        Assert.Equal(2, result[0]);
    }

    // ---- ChipForHop: decaying chip per hop ----

    [Theory]
    [InlineData(100, 60, 60, 0, 60)]   // hop 0: 60% of 100 = 60
    [InlineData(100, 60, 60, 1, 36)]   // hop 1: 60% of 60 = 36
    [InlineData(100, 60, 60, 2, 21)]   // hop 2: 60% of 36 = 21 (floor)
    public void ChipForHop_decays_each_hop(int origDmg, int basePct, int decayPct, int hopIndex, int expected)
        => Assert.Equal(expected, Ricochet.ChipForHop(origDmg, basePct, decayPct, hopIndex));

    [Fact]
    public void ChipForHop_floors_at_1_for_positive_damage()
        => Assert.Equal(1, Ricochet.ChipForHop(3, 60, 60, 2));

    [Fact]
    public void ChipForHop_zero_when_original_zero()
        => Assert.Equal(0, Ricochet.ChipForHop(0, 60, 60, 1));

    // ---- PickTarget determinism regression (tie-break fix) ----

    [Fact]
    public void PickTarget_tiebreak_lower_slot_when_lower_iterated_first()
    {
        // slot 2 and slot 5 are both dist 1; lower slot (2) first in array
        var slots = new[] { S(2, 6, 5), S(5, 5, 6) };
        Assert.Equal(2, Ricochet.PickTarget(victimSlot: 0, victimGx: 5, victimGy: 5, radius: 3, slots: slots));
    }
}
