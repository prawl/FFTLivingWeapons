using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ScholarRing.Grant() behaviour via FakeSparseMemory, under the PROD test-build compile (LWDEV is
/// not defined for dotnet test, matching Publish/CI's flavor).
///
/// LW-86 (owner decision 2026-07-14, after the 2026-07-11 fresh-save grant incident): under this
/// compile, Grant is a compiled no-op and NEVER writes, not even when count[260] reads 0 (formerly
/// the write case). The LWDEV flavor keeps the dev convenience grant (address and source cited in
/// ScholarRing.cs) and is untestable here by design, the established LWDEV-shell precedent (see
/// AttackCardProbeTextTests' header comment).
///
/// Remaining invariants (all still resolve to no write, since Grant is a no-op under this compile):
///   (2) count[260]==1 (already owned)      -> no write.
///   (3) count[260]==5 (many)               -> no write.
///   (4) address not Readable               -> no crash, no write.
///   (5) address Readable but not Writable  -> no crash, no write.
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

    // THE LOAD-BEARING TEST: the prod contract. Even the formerly-write case (readable, writable,
    // count==0) must produce zero writes under this compile.
    [Fact]
    public void Grant_ProdCompile_NeverWrites_EvenWhenCountZero()
    {
        var m = MakeReadableWritable(0);
        ScholarRing.Grant(m);
        Assert.Empty(m.Written);
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
