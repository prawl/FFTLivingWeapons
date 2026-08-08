using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// BandHeal (LW-149 stage G): the shared band-entry HP-heal core promoted out of the former
/// LifeSap.Policy.cs so Benediction/Renewal/SpiritualFont stop borrowing one signature's name
/// for a cross-cutting mechanic. The dormant LifeSap module and its one-line forwards were
/// deleted in LW-159 (the forward-agreement non-vacuity tests died with them); this suite is
/// now the sole pin of BandHeal's own contract -- heal sizing, the never-revive clamp, and the
/// guarded little-endian HP write.
/// </summary>
public class BandHealTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    // ---- HealAmount: round(maxHp * pct) away from zero, floor 1 ----

    [Theory]
    [InlineData(100, 25)]
    [InlineData(200, 50)]
    [InlineData(10, 3)]    // round(2.5) away from zero = 3
    [InlineData(2, 1)]     // round(0.5) away from zero = 1
    [InlineData(1, 1)]     // floor 1 (round(0.25) = 0 would be a dead grant)
    [InlineData(0, 0)]     // no maxHp -> no heal
    public void HealAmount_is_a_quarter_of_max_floor_1(int maxHp, int expected)
        => Assert.Equal(expected, BandHeal.HealAmount(maxHp, 0.25));

    // ---- NewHp: clamp at max, never revive ----

    [Theory]
    [InlineData(50, 100, 25, 75)]
    [InlineData(90, 100, 25, 100)]   // clamped at full
    [InlineData(100, 100, 25, 100)]  // already full
    [InlineData(0, 100, 25, 0)]      // dead: NEVER revive
    [InlineData(1, 100, 25, 26)]
    public void NewHp_clamps_and_never_revives(int hp, int maxHp, int heal, int expected)
        => Assert.Equal(expected, BandHeal.NewHp(hp, maxHp, heal));

    // ---- WriteHp: guarded little-endian write (pinned buffer stands in for the band) ----

    [Fact]
    public void WriteHp_writes_the_u16_little_endian()
    {
        using var band = PinnedBuf.Of(256);
        BandHeal.WriteHp(Live, band.Addr, 0x1234);
        Assert.Equal(0x34, band.Bytes[Offsets.AHp]);
        Assert.Equal(0x12, band.Bytes[Offsets.AHp + 1]);
    }

    [Fact]
    public void WriteHp_crossing_the_255_256_boundary_lands_as_one_16bit_write_never_two_torn_halves()
    {
        // The LW-145 fix 2 regression: one W16 call, never two separate W8 halves that could
        // expose a torn intermediate value to the game's own threads.
        var mem = new FakeSparseMemory();
        long entryAddr = 0x5000;
        mem.MarkWritable(entryAddr + Offsets.AHp, 2);   // production gates Writable n=2 (BandHeal.cs)

        BandHeal.WriteHp(mem, entryAddr, 260);   // 0x104: crosses the 255/256 byte boundary

        Assert.Equal(260, mem.WrittenU16[entryAddr + Offsets.AHp]);
        Assert.Contains(entryAddr + Offsets.AHp, mem.WriteOrder);
        Assert.False(mem.Written.ContainsKey(entryAddr + Offsets.AHp));       // no low-byte W8
        Assert.False(mem.Written.ContainsKey(entryAddr + Offsets.AHp + 1));   // no high-byte W8
    }
}
