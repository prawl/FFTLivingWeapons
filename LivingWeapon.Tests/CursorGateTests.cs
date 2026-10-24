using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CursorGate.Decide is LW-55's pure policy half: no memory access, just the two NARROWING-ONLY
/// gates (turn ownership, then weapon agreement) that decide whether the Attack card's cursor
/// resolve may be trusted. See CursorGate.cs's own class doc for the full gate-order and
/// sentinel-agreement rationale; this suite pins the decision matrix directly.
/// </summary>
public class CursorGateTests
{
    private const int Armed = 501;
    private const int OtherArmed = 502;

    // ---- Gate B: turn-flag precedence ----

    [Fact]
    public void Flag_not_one_refuses_NotTurnOwner_even_when_weapons_agree()
    {
        Assert.Equal(CursorRefusal.NotTurnOwner, CursorGate.Decide(Armed, Armed, turnFlag: 0));
    }

    [Fact]
    public void Flag_not_one_refuses_NotTurnOwner_even_when_weapons_ALSO_disagree()
    {
        // Precedence case: both gates would fail here, but the flag check runs first, so the
        // reported kind must be NotTurnOwner, never WeaponMismatch.
        Assert.Equal(CursorRefusal.NotTurnOwner, CursorGate.Decide(Armed, OtherArmed, turnFlag: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void Flag_any_value_other_than_exactly_one_refuses_NotTurnOwner(int flag)
    {
        Assert.Equal(CursorRefusal.NotTurnOwner, CursorGate.Decide(Armed, Armed, (byte)flag));
    }

    // ---- Gate A: sentinel matrix (flag == 1 throughout) ----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 0xFF)]
    [InlineData(0, 0xFFFF)]
    [InlineData(0xFF, 0)]
    [InlineData(0xFF, 0xFF)]
    [InlineData(0xFF, 0xFFFF)]
    [InlineData(0xFFFF, 0)]
    [InlineData(0xFFFF, 0xFF)]
    [InlineData(0xFFFF, 0xFFFF)]
    public void Both_sides_sentinel_agrees_as_unarmed_for_every_sentinel_pairing(int rosterHand, int bandWeapon)
    {
        Assert.Equal(CursorRefusal.None, CursorGate.Decide(rosterHand, bandWeapon, turnFlag: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFF)]
    [InlineData(0xFFFF)]
    public void Roster_sentinel_band_armed_is_WeaponMismatch(int rosterSentinel)
    {
        Assert.Equal(CursorRefusal.WeaponMismatch, CursorGate.Decide(rosterSentinel, Armed, turnFlag: 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFF)]
    [InlineData(0xFFFF)]
    public void Roster_armed_band_sentinel_is_WeaponMismatch(int bandSentinel)
    {
        Assert.Equal(CursorRefusal.WeaponMismatch, CursorGate.Decide(Armed, bandSentinel, turnFlag: 1));
    }

    [Fact]
    public void Both_armed_and_equal_is_None()
    {
        Assert.Equal(CursorRefusal.None, CursorGate.Decide(Armed, Armed, turnFlag: 1));
    }

    [Fact]
    public void Both_armed_and_unequal_is_WeaponMismatch()
    {
        Assert.Equal(CursorRefusal.WeaponMismatch, CursorGate.Decide(Armed, OtherArmed, turnFlag: 1));
    }

    // ---- Attack hand: which weapon the row names ----

    [Theory]
    [InlineData(37, 255, 37, false)]
    [InlineData(255, 37, 37, true)]
    [InlineData(0xFFFF, 0, 0xFFFF, false)]
    [InlineData(37, 36, 37, false)]
    [InlineData(255, 255, 255, false)]
    public void AttackHand_picks_main_then_off_then_unchanged(int right, int off, int hand, bool fromOff)
    {
        Assert.Equal((hand, fromOff), CursorGate.AttackHand(right, off));
    }

    [Fact]
    public void Off_hand_roster_weapon_with_sentinel_band_is_accepted()
    {
        Assert.Equal(CursorRefusal.None, CursorGate.Decide(Armed, 0xFF, turnFlag: 1, rosterHandFromOffHand: true));
    }

    [Fact]
    public void Same_weapon_without_the_off_hand_flag_and_sentinel_band_stays_WeaponMismatch()
    {
        Assert.Equal(CursorRefusal.WeaponMismatch, CursorGate.Decide(Armed, 0xFF, turnFlag: 1));
    }

    [Fact]
    public void Off_hand_flag_does_not_excuse_a_different_armed_band_weapon_or_a_fallen_turn_flag()
    {
        Assert.Equal(CursorRefusal.WeaponMismatch, CursorGate.Decide(Armed, OtherArmed, turnFlag: 1, rosterHandFromOffHand: true));
        Assert.Equal(CursorRefusal.NotTurnOwner, CursorGate.Decide(Armed, 0xFF, turnFlag: 0, rosterHandFromOffHand: true));
    }
}
