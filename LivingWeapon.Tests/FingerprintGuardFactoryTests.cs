using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// FingerprintGuard's static landmark factories (PeBuildKey, ByteSignature), exercised over a
/// tiny dictionary-backed GuardTryRead delegate (no repo fake: the portable core has zero project
/// dependencies, so its factory tests stay just as portable). Covers the boot-window safety
/// rules: a failed or short read is Unreadable (never an out-of-range index), and an all-zero
/// buffer is Unreadable rather than Mismatch (the documented uninitialized-memory escape).
/// </summary>
public class FingerprintGuardFactoryTests
{
    /// <summary>Minimal GuardTryRead over an exact-address byte-array store: seeded addresses
    /// return their bytes, everything else fails the read (mirrors Mem's fail-safe contract).</summary>
    private static GuardTryRead DictReader(Dictionary<long, byte[]> store) =>
        (long addr, int len, out byte[] buf) =>
        {
            if (store.TryGetValue(addr, out var seeded)) { buf = seeded; return true; }
            buf = System.Array.Empty<byte>();
            return false;
        };

    private static void WriteU32(Dictionary<long, byte[]> store, long addr, uint value) =>
        store[addr] = new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

    // ---- PeBuildKey ----

    private const long BaseAddr = 0x140000000L;
    private const uint ELfanew = 0x100;   // an arbitrary plausible e_lfanew offset

    [Fact]
    public void PeBuildKey_match()
    {
        var store = new Dictionary<long, byte[]>();
        WriteU32(store, BaseAddr + 0x3C, ELfanew);
        WriteU32(store, BaseAddr + ELfanew + 8, 0xAAAAAAAA);
        WriteU32(store, BaseAddr + ELfanew + 0x50, 0xBBBBBBBB);
        var landmark = FingerprintGuard.PeBuildKey(DictReader(store), BaseAddr, 0xAAAAAAAA, 0xBBBBBBBB);

        Assert.Equal(LandmarkVerdict.Match, landmark.Probe());
    }

    [Theory]
    [InlineData(0xFFFFFFFF, 0xBBBBBBBB)]
    [InlineData(0xAAAAAAAA, 0xFFFFFFFF)]
    public void PeBuildKey_mismatch_on_either_field(uint actualTimeDateStamp, uint actualSizeOfImage)
    {
        var store = new Dictionary<long, byte[]>();
        WriteU32(store, BaseAddr + 0x3C, ELfanew);
        WriteU32(store, BaseAddr + ELfanew + 8, actualTimeDateStamp);
        WriteU32(store, BaseAddr + ELfanew + 0x50, actualSizeOfImage);
        var landmark = FingerprintGuard.PeBuildKey(DictReader(store), BaseAddr, 0xAAAAAAAA, 0xBBBBBBBB);

        Assert.Equal(LandmarkVerdict.Mismatch, landmark.Probe());
    }

    [Fact]
    public void PeBuildKey_unreadable_on_any_failed_read()
    {
        var store = new Dictionary<long, byte[]>();   // nothing seeded: even e_lfanew fails
        var landmark = FingerprintGuard.PeBuildKey(DictReader(store), BaseAddr, 0xAAAAAAAA, 0xBBBBBBBB);

        Assert.Equal(LandmarkVerdict.Unreadable, landmark.Probe());
    }

    [Fact]
    public void PeBuildKey_garbage_elfanew_stays_unreadable()
    {
        var store = new Dictionary<long, byte[]>();
        WriteU32(store, BaseAddr + 0x3C, 0x7FFFFFFF);   // huge e_lfanew; the follow-up reads are never seeded
        var landmark = FingerprintGuard.PeBuildKey(DictReader(store), BaseAddr, 0xAAAAAAAA, 0xBBBBBBBB);

        Assert.Equal(LandmarkVerdict.Unreadable, landmark.Probe());
    }

    // ---- ByteSignature ----

    private static readonly byte[] Expected4 = { 1, 2, 3, 4 };

    [Fact]
    public void ByteSignature_match()
    {
        var store = new Dictionary<long, byte[]> { [0x1000] = new byte[] { 1, 2, 3, 4 } };
        var landmark = FingerprintGuard.ByteSignature(DictReader(store), 0x1000, Expected4, "sig");

        Assert.Equal(LandmarkVerdict.Match, landmark.Probe());
    }

    [Fact]
    public void ByteSignature_mismatch()
    {
        var store = new Dictionary<long, byte[]> { [0x1000] = new byte[] { 1, 2, 3, 5 } };
        var landmark = FingerprintGuard.ByteSignature(DictReader(store), 0x1000, Expected4, "sig");

        Assert.Equal(LandmarkVerdict.Mismatch, landmark.Probe());
    }

    [Fact]
    public void ByteSignature_unreadable_on_failed_read()
    {
        var store = new Dictionary<long, byte[]>();   // nothing seeded at 0x1000
        var landmark = FingerprintGuard.ByteSignature(DictReader(store), 0x1000, Expected4, "sig");

        Assert.Equal(LandmarkVerdict.Unreadable, landmark.Probe());
    }

    [Fact]
    public void ByteSignature_all_zero_read_is_unreadable()
    {
        var store = new Dictionary<long, byte[]> { [0x1000] = new byte[] { 0, 0, 0, 0 } };
        var landmark = FingerprintGuard.ByteSignature(DictReader(store), 0x1000, Expected4, "sig");

        Assert.Equal(LandmarkVerdict.Unreadable, landmark.Probe());
    }

    [Fact]
    public void ByteSignature_short_read_is_unreadable()
    {
        // The delegate returns true (a "successful" read) but the buffer is shorter than expected:
        // must never index past its length, and must report Unreadable, not Mismatch.
        GuardTryRead reader = (long addr, int len, out byte[] buf) => { buf = new byte[] { 1, 2 }; return true; };
        var landmark = FingerprintGuard.ByteSignature(reader, 0x1000, Expected4, "sig");

        Assert.Equal(LandmarkVerdict.Unreadable, landmark.Probe());
    }
}
