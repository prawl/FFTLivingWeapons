using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The guarded single-bit writes (the mutation half the pure Signatures encoders feed).
/// Driven against pinned buffers -- Mem's RPM/WPM work on any committed address in our
/// own process, so the guard discipline is exercised for real.
/// </summary>
public class MemBitsTests
{
    [Fact]
    public void OrSet_sets_the_bit_preserves_neighbors_and_reads_back()
    {
        using var pin = PinnedBuf.Of(16);
        pin.Bytes[3] = 0x40;   // a neighboring movement bit the player owns (e.g. Move +2)
        long addr = pin.Addr + 3;
        Assert.True(MemBits.OrSet(addr, 0x01, out _));    // OR-set Lifefont
        Assert.Equal(0x41, pin.Bytes[3]);                 // neighbor preserved, bit set
        Assert.True(MemBits.OrSet(addr, 0x01, out _));    // idempotent re-hold
        Assert.Equal(0x41, pin.Bytes[3]);
    }

    [Fact]
    public void OrSet_reports_the_pre_write_state()
    {
        // The pre-OR state is the LIVE-TEST SIGNAL: a bit found set means the engine KEPT it
        // since the last hold; a bit found clear means the engine wiped it and we re-armed.
        // The post-write read-back alone always says SET on a writable page -- degenerate.
        using var pin = PinnedBuf.Of(16);
        long addr = pin.Addr + 3;
        Assert.True(MemBits.OrSet(addr, 0x01, out bool wasSet));   // first hold: bit absent
        Assert.False(wasSet);
        Assert.True(MemBits.OrSet(addr, 0x01, out wasSet));        // engine kept it -> HELD
        Assert.True(wasSet);
        pin.Bytes[3] = 0;                                          // engine cleared it between holds
        Assert.True(MemBits.OrSet(addr, 0x01, out wasSet));        // re-armed -> REARMED
        Assert.False(wasSet);
    }

    [Fact]
    public void Clear_clears_only_the_target_bit_and_reads_back()
    {
        using var pin = PinnedBuf.Of(16);
        pin.Bytes[3] = 0x41;   // our granted bit 0x01 + a neighboring support the player owns (0x40)
        long addr = pin.Addr + 3;
        Assert.True(MemBits.Clear(addr, 0x01));
        Assert.Equal(0x40, pin.Bytes[3]);                 // neighbor untouched
        Assert.True(MemBits.Clear(addr, 0x01));           // idempotent re-clear
        Assert.Equal(0x40, pin.Bytes[3]);
    }
}
