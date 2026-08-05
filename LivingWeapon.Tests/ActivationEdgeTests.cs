using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-149 stage C: pins <see cref="ActivationEdge.Step"/>, the ref-bool helper extracted from the
/// 14+2-site <c>if (active != _wasActive) { _wasActive = active; ... }</c> idiom every signature
/// module hand-rolled. This class owns ONLY the edge-detection cadence -- it never logs -- so these
/// tests pin exactly that: true on a transition (and only a transition), false on steady state, and
/// the ref write landing (or not landing) to match the old inline assignment byte-for-byte.
/// </summary>
public class ActivationEdgeTests
{
    [Fact]
    public void Step_returns_true_on_the_rising_edge_false_to_true()
    {
        bool wasActive = false;
        Assert.True(ActivationEdge.Step(ref wasActive, true));
    }

    [Fact]
    public void Step_returns_true_on_the_falling_edge_true_to_false()
    {
        bool wasActive = true;
        Assert.True(ActivationEdge.Step(ref wasActive, false));
    }

    [Fact]
    public void Step_returns_false_on_steady_state_true()
    {
        bool wasActive = true;
        Assert.False(ActivationEdge.Step(ref wasActive, true));
    }

    [Fact]
    public void Step_returns_false_on_steady_state_false()
    {
        bool wasActive = false;
        Assert.False(ActivationEdge.Step(ref wasActive, false));
    }

    [Fact]
    public void Step_flips_wasActive_to_the_new_value_by_ref_on_a_transition()
    {
        bool wasActive = false;
        ActivationEdge.Step(ref wasActive, true);
        Assert.True(wasActive);

        ActivationEdge.Step(ref wasActive, false);
        Assert.False(wasActive);
    }

    [Fact]
    public void Step_leaves_wasActive_untouched_on_steady_state()
    {
        // Mirrors the old idiom: the assignment only ever happens inside the `if`, so a
        // steady-state call must not so much as re-stamp the same value.
        bool wasActive = true;
        ActivationEdge.Step(ref wasActive, true);
        Assert.True(wasActive);

        wasActive = false;
        ActivationEdge.Step(ref wasActive, false);
        Assert.False(wasActive);
    }

    [Fact]
    public void Step_cadence_matches_the_old_inline_idiom_across_a_transition_sequence()
    {
        // active sequence: false, false, true, true, true, false, true
        // expected Step:      -     F     T     F     F     T     T
        bool[] activeSeq = { false, false, true, true, true, false, true };
        bool[] expectedEdge = { false, false, true, false, false, true, true };

        bool wasActive = false;   // matches every module's field default
        for (int i = 0; i < activeSeq.Length; i++)
        {
            bool edge = ActivationEdge.Step(ref wasActive, activeSeq[i]);
            Assert.Equal(expectedEdge[i], edge);
            Assert.Equal(activeSeq[i], wasActive);   // wasActive always tracks active after Step
        }
    }
}
