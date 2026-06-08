using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure turn-counting behind Galewind's charm-lock. We count the locked enemy's own turns off
/// its CT (charge time, +0x25 on the struct we hold): CT climbs to full, sits there during the turn,
/// then resets when it acts. A turn = a reset from (near-)full to notably lower. The memory
/// reads/writes (find the copy, hold the bytes) are integration; this nails the count + clear timing.
/// </summary>
public class CharmLockTests
{
    [Theory]
    [InlineData(100, 10, true)]    // full -> reset = a completed turn
    [InlineData(95, 0, true)]
    [InlineData(90, 69, true)]     // dropped below the floor
    [InlineData(90, 70, false)]    // not a big enough drop (still mid/charging)
    [InlineData(80, 5, false)]     // wasn't full when it dropped -> not our reset edge
    [InlineData(50, 100, false)]   // climbing, not a turn
    [InlineData(100, 100, false)]  // still full (mid-turn), not reset yet
    [InlineData(0, 0, false)]
    public void IsTurn_detects_a_CT_reset_from_full(int last, int cur, bool expected)
    {
        Assert.Equal(expected, CharmLock.IsTurn(last, cur));
    }
}
