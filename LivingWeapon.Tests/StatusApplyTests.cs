using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-58 status-apply pure math. Every bit assertion cross-checks against an already-proven
/// Offsets constant (Dead/Poison/Haste observed live), so the decoded status-id layout is tied to
/// the shipped, live-confirmed status offsets rather than asserted in a vacuum.
/// </summary>
public class StatusApplyTests
{
    [Theory]
    [InlineData(8, 0)]     // first enemy seat -> engine slot 0 (owner census 2026-07-09)
    [InlineData(12, 4)]    // the deathdiff/inflict victim s12
    [InlineData(17, 9)]    // last typical enemy seat
    [InlineData(24, 16)]   // CombatAnchor (Ramza) -> slot 16 (BattleUnitsBase + 0x2000)
    public void EngineSlot_is_band_seat_minus_8(int seat, int expected)
        => Assert.Equal(expected, StatusApply.EngineSlot(seat));

    [Fact]
    public void BattleUnitsBase_is_two_arrays_below_the_combat_anchor()
        => Assert.Equal(0x141853CE0L, StatusApply.BattleUnitsBase);

    [Theory]
    [InlineData(0, false)]    // engine slot -8: below BattleUnitsBase, would AV
    [InlineData(7, false)]    // engine slot -1: still off the array
    [InlineData(8, true)]     // engine slot 0: the first real enemy
    [InlineData(24, true)]    // engine slot 16: Ramza
    [InlineData(28, true)]    // engine slot 20: MaxEngineSlot, last callable
    [InlineData(29, false)]   // engine slot 21: past the array
    public void IsCallableSeat_rejects_seats_that_map_off_the_array(int seat, bool callable)
        => Assert.Equal(callable, StatusApply.IsCallableSeat(seat));

    [Fact]
    public void EngineSlot_is_negative_for_the_sub_array_scratch_seats()
        => Assert.True(StatusApply.EngineSlot(0) < 0 && StatusApply.EngineSlot(7) < 0);

    [Fact]
    public void Dead_id_matches_the_proven_dead_status_byte_and_bit()
    {
        Assert.Equal(0, StatusApply.StatusByte(StatusApply.DeadId));
        Assert.Equal(Offsets.ADeadBit, StatusApply.StatusMask(StatusApply.DeadId));
        Assert.Equal(Offsets.ADeadStatus, StatusApply.Composed + StatusApply.StatusByte(StatusApply.DeadId));
    }

    [Fact]
    public void Poison_id_matches_the_proven_poison_byte_and_bit()
    {
        Assert.Equal(Offsets.APoisonBit, StatusApply.StatusMask(StatusApply.PoisonId));
        Assert.Equal(Offsets.APoison, StatusApply.Composed + StatusApply.StatusByte(StatusApply.PoisonId));
    }

    [Fact]
    public void Haste_id_matches_the_proven_haste_byte_and_bit()
    {
        Assert.Equal(Offsets.AHasteBit, StatusApply.StatusMask(StatusApply.HasteId));
        Assert.Equal(Offsets.AHaste, StatusApply.Composed + StatusApply.StatusByte(StatusApply.HasteId));
    }

    [Fact]
    public void Treasure_id_15_lands_on_the_conversion_marker_byte()
    {
        // Composed +0x46 / 0x01 is the marker the s15 chest pop flipped (deathdiff 2026-07-09).
        Assert.Equal(0x46, StatusApply.Composed + StatusApply.StatusByte(StatusApply.TreasureId));
        Assert.Equal(0x01, StatusApply.StatusMask(StatusApply.TreasureId));
    }

    [Fact]
    public void Treasure_pending_bit_lands_where_the_pop_transient_was_seen()
        // The deathdiff tape saw the pop queue band +0x1C0 = 0x01 (id 15 in the pending field).
        => Assert.Equal(0x1C0, StatusApply.PendingAdd + StatusApply.StatusByte(StatusApply.TreasureId));

    [Fact]
    public void Inflicted_layer_sits_the_proven_0x18E_mirror_above_the_composed_layer()
        => Assert.Equal(0x18E, StatusApply.Inflicted - StatusApply.Composed);

    // --- APPLIED verdict: newly-set-only, the correctness fix for the mode sweep ---

    [Fact]
    public void NewlyApplied_is_true_only_when_the_call_set_a_previously_absent_bit()
        => Assert.True(StatusApply.NewlyApplied(0x00, 0x00, 0x08, 0x00, 0x08));

    [Fact]
    public void NewlyApplied_is_false_when_the_bit_was_already_set_before_the_call()
    {
        // A re-found target (bit persisted from a prior press) or a self-hasted enemy: no proof.
        Assert.True(StatusApply.AlreadyHeld(0x08, 0x00, 0x08));
        Assert.False(StatusApply.NewlyApplied(0x08, 0x00, 0x08, 0x00, 0x08));
        Assert.False(StatusApply.NewlyApplied(0x00, 0x08, 0x00, 0x08, 0x08));   // already in the inflicted layer
    }

    [Fact]
    public void NewlyApplied_is_false_when_the_bit_never_appeared()
        => Assert.False(StatusApply.NewlyApplied(0x00, 0x00, 0x00, 0x00, 0x08));

    [Fact]
    public void NewlyApplied_counts_a_bit_that_lands_in_either_layer()
        // The apply engine ORs into inflicted; composed re-derives from it: either proves success.
        => Assert.True(StatusApply.NewlyApplied(0x00, 0x00, 0x00, 0x08, 0x08));
}
