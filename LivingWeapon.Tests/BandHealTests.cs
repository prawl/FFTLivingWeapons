using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// BandHeal (LW-149 stage G): the shared band-entry HP-heal core promoted out of LifeSap.Policy.cs
/// so Benediction/Renewal/SpiritualFont stop borrowing one signature's name for a
/// cross-cutting mechanic. LifeSap.Policy.HealAmount/NewHp/WriteHp are now one-line forwards to
/// here -- this suite pins BandHeal's own behavior (the same contract LifeSapTests already pins on
/// the forward) AND, separately, proves the forwards genuinely delegate rather than merely
/// agreeing by coincidence (a forward that silently diverged would still pass a same-suite
/// duplicate-assertions check; only a direct LifeSap-vs-BandHeal comparison catches that).
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
        // Same LW-145 fix 2 regression LifeSapTests pins on the forward: one W16 call, never two
        // separate W8 halves that could expose a torn intermediate value to the game's own threads.
        var mem = new FakeSparseMemory();
        long entryAddr = 0x5000;
        mem.MarkWritable(entryAddr + Offsets.AHp, 2);   // production gates Writable n=2 (BandHeal.cs)

        BandHeal.WriteHp(mem, entryAddr, 260);   // 0x104: crosses the 255/256 byte boundary

        Assert.Equal(260, mem.WrittenU16[entryAddr + Offsets.AHp]);
        Assert.Contains(entryAddr + Offsets.AHp, mem.WriteOrder);
        Assert.False(mem.Written.ContainsKey(entryAddr + Offsets.AHp));       // no low-byte W8
        Assert.False(mem.Written.ContainsKey(entryAddr + Offsets.AHp + 1));   // no high-byte W8
    }

    // ---- Non-vacuity: LifeSap.Policy's forwards genuinely delegate, not just agree by luck ----

    [Theory]
    [InlineData(100, 0.25)]
    [InlineData(10, 0.25)]
    [InlineData(1, 0.25)]
    [InlineData(0, 0.25)]
    [InlineData(333, 0.1)]
    public void LifeSap_HealAmount_and_BandHeal_HealAmount_agree_on_sample_inputs(int maxHp, double pct)
        => Assert.Equal(BandHeal.HealAmount(maxHp, pct), LifeSap.HealAmount(maxHp, pct));

    [Theory]
    [InlineData(50, 100, 25)]
    [InlineData(0, 100, 25)]
    [InlineData(100, 100, 25)]
    [InlineData(1, 100, 25)]
    public void LifeSap_NewHp_and_BandHeal_NewHp_agree_on_sample_inputs(int hp, int maxHp, int heal)
        => Assert.Equal(BandHeal.NewHp(hp, maxHp, heal), LifeSap.NewHp(hp, maxHp, heal));

    [Fact]
    public void LifeSap_WriteHp_and_BandHeal_WriteHp_produce_identical_writes_on_a_fake()
    {
        // Two independent fakes, the same call shape through each name -- if LifeSap.WriteHp
        // truly forwards to BandHeal.WriteHp, both fakes end up byte-identical, including the
        // guard shape (Writable gate, one W16 call). A reimplementation that merely computed the
        // same final value could still diverge here on write count/order; only a real forward
        // guarantees this passes.
        var a = new FakeSparseMemory();
        var b = new FakeSparseMemory();
        long addr = 0x5000 + Offsets.AHp;
        a.MarkWritable(addr, 2);   // production gates Writable n=2 (BandHeal.cs)
        b.MarkWritable(addr, 2);

        LifeSap.WriteHp(a, 0x5000, 260);
        BandHeal.WriteHp(b, 0x5000, 260);

        Assert.Equal(260, a.WrittenU16[addr]);
        Assert.Equal(a.WrittenU16[addr], b.WrittenU16[addr]);
        Assert.Equal(a.U8s[addr], b.U8s[addr]);
        Assert.Equal(a.U8s[addr + 1], b.U8s[addr + 1]);
        Assert.Equal(a.WriteOrder, b.WriteOrder);
    }
}
