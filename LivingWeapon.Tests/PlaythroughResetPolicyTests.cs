using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure LW-51 Tier-1 opening-detector predicate. eventId == OpeningEventId (2) alone is not
/// enough: it also aliases the acting unit's nameId during combat animations, so the predicate
/// gates on battleMode == 0 AND !inLive too (a mid-battle dialogue frame reads battleMode == 0
/// but inLive stays true via BattleState.InLiveBattle's excused-marker path).
/// </summary>
public class PlaythroughResetPolicyTests
{
    [Theory]
    [InlineData(2, 0, false, true)]        // the opening, genuinely out of battle
    [InlineData(2, 0, true, false)]        // mid-battle dialogue aliasing event 2
    [InlineData(2, 3, false, false)]       // in battle (action menu)
    [InlineData(2, 3, true, false)]        // in battle, inLive too
    [InlineData(5, 0, false, false)]       // not the opening event id
    [InlineData(0xFFFF, 0, false, false)]  // the unset/menu sentinel
    public void IsOpeningOutOfBattle_truth_table(int eventId, int battleMode, bool inLive, bool expected)
        => Assert.Equal(expected, PlaythroughResetPolicy.IsOpeningOutOfBattle(eventId, battleMode, inLive));

    [Fact]
    public void OpeningEventId_is_2()
        => Assert.Equal(2, PlaythroughResetPolicy.OpeningEventId);

    [Fact]
    public void HoldTicks_is_a_positive_debounce_window()
        => Assert.True(PlaythroughResetPolicy.HoldTicks > 1);
}
