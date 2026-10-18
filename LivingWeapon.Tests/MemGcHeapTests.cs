using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>The region walk must never offer the mod's own managed heap to a sweep.</summary>
public class MemGcHeapTests
{
    [Theory]
    [InlineData(0x5000L, 0x5000L, false)]
    [InlineData(0x6000L, 0x5000L, true)]
    [InlineData(0x6000L, 0L, true)]
    [InlineData(0L, 0L, true)]
    public void ShouldSweep_SkipsOnlyTheGcReservation(long region, long gc, bool sweeps)
        => Assert.Equal(sweeps, Mem.ShouldSweep(region, gc));

    [Fact]
    public void GcHeapAllocationBase_IsFound()
        => Assert.NotEqual(0L, Mem.GcHeapAllocationBase);

    [Fact]
    public void Regions_NeverCoverAFreshManagedArray()
    {
        var arr = new byte[4096];
        var h = GCHandle.Alloc(arr, GCHandleType.Pinned);
        try
        {
            long addr = (long)h.AddrOfPinnedObject();
            Assert.DoesNotContain(Mem.Regions(), r => addr >= r.baseAddr && addr < r.baseAddr + r.size);
        }
        finally { h.Free(); }
    }

    [Fact]
    public void Regions_StillYieldsNativeCommittedMemory()
    {
        long native = (long)Marshal.AllocHGlobal(1 << 20);
        try
        {
            Assert.Contains(Mem.Regions(), r => native >= r.baseAddr && native < r.baseAddr + r.size);
        }
        finally { Marshal.FreeHGlobal((nint)native); }
    }
}
