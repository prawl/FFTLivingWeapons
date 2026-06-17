using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ScholarRing.Grant() behaviour via FakeSparseMemory.
///
/// Invariants:
///   (1) count[260]==0 and address is Readable+Writable  -> writes 1 to 0x1411A7C00+260.
///   (2) count[260]==1 (already owned)                  -> no write (idempotent).
///   (3) count[260]==5 (many)                           -> no write (idempotent).
///   (4) address not Readable                           -> no crash, no write.
///   (5) address Readable but not Writable              -> no crash, no write.
///   (6) address is Readable+Writable+count==0          -> written value is exactly 1.
///   (7) written address is exactly 0x1411A7C00+260.
/// </summary>
public class ScholarRingTests
{
    private const long Base    = Offsets.InventoryCountBase;
    private const int  ItemId  = Offsets.ScholarRingItemId;   // 260
    private const long RingAddr = Base + ItemId;

    private static FakeSparseMemory MakeReadableWritable(byte initialCount)
    {
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(RingAddr);
        m.WritableAddrs.Add(RingAddr);
        m.U8s[RingAddr] = initialCount;
        return m;
    }

    [Fact]
    public void GrantWhenZero_WritesOne()
    {
        var m = MakeReadableWritable(0);
        ScholarRing.Grant(m);
        Assert.True(m.Written.ContainsKey(RingAddr), "should have written to the ring addr");
        Assert.Equal((byte)1, m.Written[RingAddr]);
    }

    [Fact]
    public void GrantWhenZero_WritesAtExactAddress()
    {
        var m = MakeReadableWritable(0);
        ScholarRing.Grant(m);
        // exactly one address written, and it is the ring address
        Assert.Single(m.Written);
        Assert.True(m.Written.ContainsKey(RingAddr));
        Assert.Equal(0x1411A7C00L + 260, RingAddr);
    }

    [Fact]
    public void GrantWhenAlreadyOne_NoWrite()
    {
        var m = MakeReadableWritable(1);
        ScholarRing.Grant(m);
        Assert.Empty(m.Written);
    }

    [Fact]
    public void GrantWhenMany_NoWrite()
    {
        var m = MakeReadableWritable(5);
        ScholarRing.Grant(m);
        Assert.Empty(m.Written);
    }

    [Fact]
    public void NotReadable_NoCrashNoWrite()
    {
        var m = new FakeSparseMemory();
        // address not in ReadableAddrs, not in WritableAddrs, count defaults to 0
        ScholarRing.Grant(m);
        Assert.Empty(m.Written);
    }

    [Fact]
    public void ReadableButNotWritable_NoCrashNoWrite()
    {
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(RingAddr);
        m.U8s[RingAddr] = 0;
        // WritableAddrs NOT populated
        ScholarRing.Grant(m);
        Assert.Empty(m.Written);
    }
}
