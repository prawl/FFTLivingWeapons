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

    // ---- LW-145 fix 1: the core logic over the IGameMemory seam, so a FAILED pre-read is a
    // provable, constructible fake state -- distinct from a real zero byte -- instead of a
    // hypothetical. FakeSparseMemory: Writable() is address-marked independent of TryReadBytes,
    // which only succeeds for an address covered by a TerrainBlocks entry, so "writable but the
    // read itself fails" is exactly the case the old Mem.U8-based pre-read could never see. ----

    [Fact]
    public void OrSet_over_IGameMemory_load_bearing_failed_pre_read_writes_nothing()
    {
        // THE non-vacuity target. The old code did `cur = Mem.U8(addr)`, which fails safe to 0 on
        // an unreadable page -- so a failed read here would have written the BARE MASK, zeroing
        // the other 7 bits: the exact neighbor-bit disturbance the class doc forbids.
        var mem = new FakeSparseMemory();
        long addr = 0x1000;
        mem.WritableAddrs.Add(addr);   // writable ...
        // ... but NOT backed by any TerrainBlocks entry, so TryReadBytes fails.

        bool ok = MemBits.OrSet(mem, addr, 0x01, out bool wasSet);

        Assert.False(ok);
        Assert.False(wasSet);
        Assert.DoesNotContain(addr, mem.WriteOrder);
        Assert.Empty(mem.Written);
        Assert.Empty(mem.WrittenU16);
        Assert.Empty(mem.WrittenBytes);
    }

    [Fact]
    public void Clear_over_IGameMemory_load_bearing_failed_pre_read_writes_nothing_and_never_claims_clear()
    {
        // The dual defect: the old failed-read-as-0 both skipped the write (0 & mask == 0, "already
        // clear") AND reported success (the post-read failed the same way, also reading 0) -- an
        // unverified answer dressed as a confirmed one.
        var mem = new FakeSparseMemory();
        long addr = 0x1000;
        mem.WritableAddrs.Add(addr);

        bool ok = MemBits.Clear(mem, addr, 0x01);

        Assert.False(ok);
        Assert.Empty(mem.Written);
        Assert.Empty(mem.WriteOrder);
    }

    [Fact]
    public void OrSet_over_IGameMemory_success_path_sets_only_the_mask_bit_preserving_neighbors()
    {
        var mem = new FakeSparseMemory();
        long addr = 0x2000;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 0x40;                    // a neighboring bit the player owns
        mem.TerrainBlocks[addr] = new byte[] { 0x40 };   // makes TryReadBytes(addr,1,...) succeed

        bool ok = MemBits.OrSet(mem, addr, 0x01, out bool wasSet);

        Assert.True(ok);
        Assert.False(wasSet);
        Assert.Equal((byte)0x41, mem.U8(addr));   // neighbor preserved, bit set
    }

    [Fact]
    public void Clear_over_IGameMemory_success_path_on_an_already_clear_bit_issues_no_write()
    {
        var mem = new FakeSparseMemory();
        long addr = 0x3000;
        mem.WritableAddrs.Add(addr);
        mem.U8s[addr] = 0x40;                    // bit 0x01 already clear
        mem.TerrainBlocks[addr] = new byte[] { 0x40 };

        bool ok = MemBits.Clear(mem, addr, 0x01);

        Assert.True(ok);
        Assert.Empty(mem.Written);   // no write issued: the bit already reads clear
        Assert.Equal((byte)0x40, mem.U8(addr));
    }
}
