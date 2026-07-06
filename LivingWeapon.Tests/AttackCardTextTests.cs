using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// AttackCardText now holds only the census-proven vanilla desc constant and its footprint budget
/// (LW-31 stage 3): the compose responsibility it used to own (stage 2's flat
/// "{name}{suffix} Kills: N." line) is superseded by AttackRow.Policy.ComposeRow (the row rename)
/// plus AttackCardTail.ComposeTail (the description tail); see AttackCardTailTests.cs for the
/// ported clause-priority/budget-edge coverage.
/// </summary>
public class AttackCardTextTests
{
    [Fact]
    public void VanillaDesc_is_exactly_73_chars()
    {
        Assert.Equal(73, AttackCardText.VanillaDesc.Length);
        Assert.Equal(AttackCardText.DefaultBudgetChars, AttackCardText.VanillaDesc.Length);
    }
}
