using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// CreditGate.Decide is LW-56's pure policy half: no memory access, just a plain partition of a
/// culprit weapon list into survivors/refused by a predicate. See CreditGate.cs's own class doc
/// for the narrowing-only contract (mirrors CursorGate.cs); this suite pins the truth table.
/// </summary>
public class CreditGateTests
{
    [Fact]
    public void All_ids_pass_survivors_equal_input_refused_empty()
    {
        var culprit = new List<int> { 9, 52, 76 };

        var (survivors, refused) = CreditGate.Decide(culprit, _ => true);

        Assert.Equal(culprit, survivors);
        Assert.Empty(refused);
    }

    [Fact]
    public void No_id_passes_survivors_empty_refused_equal_input()
    {
        var culprit = new List<int> { 9, 52, 76 };

        var (survivors, refused) = CreditGate.Decide(culprit, _ => false);

        Assert.Empty(survivors);
        Assert.Equal(culprit, refused);
    }

    [Fact]
    public void Mixed_partition_is_exact_and_preserves_input_order_in_both_outputs()
    {
        var culprit = new List<int> { 9, 52, 76, 30 };

        var (survivors, refused) = CreditGate.Decide(culprit, id => id == 52 || id == 30);

        Assert.Equal(new List<int> { 52, 30 }, survivors);
        Assert.Equal(new List<int> { 9, 76 }, refused);
    }

    [Fact]
    public void Empty_input_produces_empty_outputs()
    {
        var (survivors, refused) = CreditGate.Decide(new List<int>(), _ => true);

        Assert.Empty(survivors);
        Assert.Empty(refused);
    }
}
