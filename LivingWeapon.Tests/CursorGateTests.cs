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
}
