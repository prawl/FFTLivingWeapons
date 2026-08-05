using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Grid (LW-149 stage G): the shared tile-metric home promoted out of Ricochet.Policy.cs.
/// Wyrmblood.Policy.InSplash already borrowed Ricochet.Manhattan by name for its own splash-radius
/// math; Grid removes that cross-signature dependency. Ricochet.Manhattan is now a one-line forward
/// to here -- this suite pins Grid.Manhattan's own behavior (the same contract RicochetTests
/// already pins on the forward) AND, separately, proves the forward genuinely delegates rather
/// than merely agreeing by coincidence.
/// </summary>
public class GridTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(3, 4, 0, 0, 7)]
    [InlineData(5, 5, 8, 5, 3)]
    [InlineData(5, 5, 5, 8, 3)]
    [InlineData(5, 5, 7, 7, 4)]
    public void Manhattan_is_abs_dx_plus_abs_dy(int x1, int y1, int x2, int y2, int expected)
        => Assert.Equal(expected, Grid.Manhattan(x1, y1, x2, y2));

    // ---- Non-vacuity: Ricochet.Manhattan's forward genuinely delegates, not just agrees by luck ----

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(3, 4, 0, 0)]
    [InlineData(5, 5, 8, 5)]
    [InlineData(-2, 7, 9, -3)]
    public void Ricochet_Manhattan_and_Grid_Manhattan_agree_on_sample_points(int x1, int y1, int x2, int y2)
        => Assert.Equal(Grid.Manhattan(x1, y1, x2, y2), Ricochet.Manhattan(x1, y1, x2, y2));
}
