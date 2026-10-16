using System;
using LivingWeapon;
using Xunit;
using Xunit.Abstractions;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-387: the Denuvo-free 1.5.2 build turned ASLR on, so every pinned address is a preferred-base
/// absolute rebased through <see cref="ImageBase"/>. These pin the rebase arithmetic, the input
/// validation, and that the production pin sites actually follow the base. ImageBase is process-wide
/// static state; every test that mutates it restores the preferred base in a finally block (the
/// assembly already runs tests serially, TestLoggingSetup.cs).
/// </summary>
public class ImageBaseTests
{
    private readonly ITestOutputHelper _out;
    public ImageBaseTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void TryResolveLive_returns_an_aligned_positive_base_in_process()
    {
        Assert.True(ImageBase.TryResolveLive(out long liveBase));
        _out.WriteLine($"testhost base = 0x{liveBase:X}");
        Assert.True(liveBase > 0);
        Assert.Equal(0L, liveBase & 0xFFFF);
    }
    private const long Relocated = 0x7FF6_1234_0000L;

    [Fact]
    public void Default_base_is_the_preferred_base()
    {
        ImageBase.ResetForTests();
        Assert.Equal(0x140000000L, ImageBase.Preferred);
        Assert.Equal(ImageBase.Preferred, ImageBase.Base);
        Assert.Equal(0L, ImageBase.Delta);
        Assert.Equal(0x1411A7D10L, ImageBase.At(0x1411A7D10L));
    }

    [Fact]
    public void Set_rebases_At_by_exactly_the_delta()
    {
        try
        {
            Assert.True(ImageBase.Set(Relocated));
            Assert.Equal(Relocated, ImageBase.Base);
            Assert.Equal(Relocated - 0x140000000L, ImageBase.Delta);
            Assert.Equal(Relocated + 0x11A7D10L, ImageBase.At(0x1411A7D10L));
        }
        finally { ImageBase.ResetForTests(); }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-0x10000L)]
    [InlineData(0x7FF612340800L)]   // positive but not 64KB-aligned
    public void Set_rejects_implausible_bases_and_leaves_Base_untouched(long bad)
    {
        try
        {
            Assert.False(ImageBase.Set(bad));
            Assert.Equal(ImageBase.Preferred, ImageBase.Base);
        }
        finally { ImageBase.ResetForTests(); }
    }

    [Fact]
    public void ResetForTests_restores_the_preferred_base()
    {
        ImageBase.Set(Relocated);
        ImageBase.ResetForTests();
        Assert.Equal(ImageBase.Preferred, ImageBase.Base);
    }

    [Fact]
    public void Pin_sites_follow_the_base()
    {
        ImageBase.ResetForTests();
        long rosterBefore = Offsets.RosterBase;
        long barrageBefore = Barrage.AbilityBase;
        long decoyBefore = ProvokePolicy.DecoyActionTable;
        long unitsBefore = StatusApply.BattleUnitsBase;
        long arrayReadBefore = Offsets.ArrayReadBase;
        try
        {
            Assert.True(ImageBase.Set(Relocated));
            long delta = ImageBase.Delta;

            Assert.Equal(ImageBase.At(0x1411A7D10L), Offsets.RosterBase);
            Assert.Equal(0x110L, Offsets.RosterBase - Offsets.InventoryCountBase);
            Assert.Equal(Offsets.ArrayBase - 19L * 0x200, Offsets.ArrayReadBase);
            Assert.Equal(rosterBefore + delta, Offsets.RosterBase);
            Assert.Equal(arrayReadBefore + delta, Offsets.ArrayReadBase);
            Assert.Equal(barrageBefore + delta, Barrage.AbilityBase);
            Assert.Equal(decoyBefore + delta, ProvokePolicy.DecoyActionTable);
            Assert.Equal(unitsBefore + delta, StatusApply.BattleUnitsBase);
        }
        finally { ImageBase.ResetForTests(); }
    }
}
