using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// FakeSparseMemory contract: a sparse address -> value IGameMemory fake. Unseeded
/// reads return 0 (Mem's fail-safe contract); W8 records the write in Written AND
/// updates U8s so read-backs observe it; Writable passes only for marked addresses.
/// </summary>
public class FakeSparseMemoryTests
{
    [Fact]
    public void Unseeded_reads_return_zero()
    {
        var m = new FakeSparseMemory();
        Assert.Equal(0, m.U8(0x1000));
        Assert.Equal(0, m.U16(0x1000));
    }

    [Fact]
    public void Seeded_u8_and_u16_read_back()
    {
        var m = new FakeSparseMemory();
        m.U8s[0x1000] = 0xAB;
        m.U16s[0x2000] = 0x1234;
        Assert.Equal(0xAB, m.U8(0x1000));
        Assert.Equal(0x1234, m.U16(0x2000));
    }

    [Fact]
    public void U8_and_u16_maps_are_independent()
    {
        // The fake is dictionary-keyed, not byte-addressed: a U8 seed at an address
        // does NOT compose into the U16 read there (and vice versa).
        var m = new FakeSparseMemory();
        m.U8s[0x1000] = 0xFF;
        Assert.Equal(0, m.U16(0x1000));
        m.U16s[0x3000] = 0xBEEF;
        Assert.Equal(0, m.U8(0x3000));
    }

    [Fact]
    public void W8_records_the_write_and_updates_the_read_back()
    {
        var m = new FakeSparseMemory();
        m.W8(0x4000, 0x42);
        Assert.Equal((byte)0x42, m.Written[0x4000]);
        Assert.Equal(0x42, m.U8(0x4000));
    }

    [Fact]
    public void Writable_passes_only_for_marked_addresses()
    {
        var m = new FakeSparseMemory();
        Assert.False(m.Writable(0x5000, 1));
        m.WritableAddrs.Add(0x5000);
        Assert.True(m.Writable(0x5000, 1));
        Assert.False(m.Writable(0x5001, 1));
    }

    // ---- LW-147/LW-151: the length-aware Readable/Writable gate. Production Mem.Probe (Mem.cs)
    // gates the WHOLE [addr, addr+len) range against one committed region; LW-147 built that
    // honesty behind an opt-in StrictRangeChecks flag, and LW-151 made it the DEFAULT for the
    // whole suite (every marking site now declares the real span the shipped code reads). These
    // pins hold the mechanism in both directions: strict semantics, the strict DEFAULT (the pin
    // that goes red if the one-line default flip is ever reverted), and the legacy opt-out. ----

    [Fact]
    public void Strict_Readable_refuses_a_multibyte_range_that_is_only_half_marked()
    {
        var m = new FakeSparseMemory { StrictRangeChecks = true };
        m.ReadableAddrs.Add(0xC000);           // only the first byte marked
        Assert.False(m.Readable(0xC000, 2));   // the second byte (0xC001) is NOT covered
    }

    [Fact]
    public void Strict_Readable_passes_a_multibyte_range_once_every_byte_is_marked()
    {
        var m = new FakeSparseMemory { StrictRangeChecks = true };
        m.ReadableAddrs.Add(0xC000);
        m.ReadableAddrs.Add(0xC001);           // two adjacent single-byte marks compose...
        Assert.True(m.Readable(0xC000, 2));    // ...into full coverage of the 2-byte range
    }

    [Fact]
    public void Strict_MarkReadable_marks_a_whole_range_in_one_call()
    {
        var m = new FakeSparseMemory { StrictRangeChecks = true };
        m.MarkReadable(0xD000, 4);
        Assert.True(m.Readable(0xD000, 4));
        Assert.False(m.Readable(0xD000, 5));   // one byte past the marked range still refuses
    }

    [Fact]
    public void Strict_Writable_refuses_a_multibyte_range_that_is_only_half_marked()
    {
        var m = new FakeSparseMemory { StrictRangeChecks = true };
        m.WritableAddrs.Add(0xE000);
        Assert.False(m.Writable(0xE000, 2));
    }

    [Fact]
    public void Strict_MarkWritable_marks_a_whole_range_in_one_call()
    {
        var m = new FakeSparseMemory { StrictRangeChecks = true };
        m.MarkWritable(0xF000, 3);
        Assert.True(m.Writable(0xF000, 3));
        Assert.False(m.Writable(0xEFFF, 3));   // one byte before the marked range still refuses
    }

    [Fact]
    public void Strict_is_the_DEFAULT_a_half_marked_multibyte_range_refuses_without_opting_in()
    {
        // THE load-bearing LW-151 pin: a fresh fake, StrictRangeChecks untouched, must already
        // enforce the honest whole-range gate production Mem.Probe enforces. Reverting the
        // one-line default flip on FakeSparseMemory turns exactly this test red.
        var m = new FakeSparseMemory();
        m.ReadableAddrs.Add(0x11000);          // only the first byte of a 2-byte field marked
        Assert.False(m.Readable(0x11000, 2));  // 0x11001 was never marked -- refuses, like production
        m.WritableAddrs.Add(0x12000);
        Assert.False(m.Writable(0x12000, 2));  // Writable's default is strict too
    }

    [Fact]
    public void Legacy_opt_out_restores_base_address_only_semantics()
    {
        // The other direction of the pin: the pre-LW-151 legacy mode is still reachable, but
        // only by explicit opt-out. Nothing in this suite runs legacy except this pin; a new
        // test should never reach for it (see the class doc).
        var m = new FakeSparseMemory { StrictRangeChecks = false };
        m.ReadableAddrs.Add(0x13000);
        Assert.True(m.Readable(0x13000, 2));   // n ignored, base membership alone passes
    }

    [Fact]
    public void WriteBytes_records_into_WrittenBytes_and_invokes_OnWrite()
    {
        var m = new FakeSparseMemory();
        var hookCalls = new System.Collections.Generic.List<(long addr, byte[] bytes)>();
        m.OnWrite = (addr, bytes) => hookCalls.Add((addr, bytes));

        m.WriteBytes(0x6000, new byte[] { 1, 2, 3, 4 });

        Assert.Single(m.WrittenBytes);
        Assert.Equal(0x6000, m.WrittenBytes[0].addr);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, m.WrittenBytes[0].bytes);
        Assert.Single(hookCalls);
        Assert.Equal(0x6000, hookCalls[0].addr);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, hookCalls[0].bytes);
    }

    [Fact]
    public void WriteBytes_works_without_OnWrite_hook_wired()
    {
        var m = new FakeSparseMemory();
        m.WriteBytes(0x7000, new byte[] { 9 });
        Assert.Single(m.WrittenBytes);
    }

    // ---- LW-145 fix 0 (LW-147 slice): W16/WriteBytes join the read-back + WriteOrder contract ----

    [Fact]
    public void W16_mirrors_into_U8s_little_endian_and_lands_in_WriteOrder()
    {
        var m = new FakeSparseMemory();
        m.W16(0x8000, 0x1234);
        Assert.Equal(0x1234, m.U16(0x8000));
        Assert.Equal((byte)0x34, m.U8(0x8000));   // low byte
        Assert.Equal((byte)0x12, m.U8(0x8001));   // high byte
        Assert.Contains(0x8000L, m.WriteOrder);
    }

    [Fact]
    public void WriteBytes_applies_into_U8s_so_a_U8_read_back_observes_it()
    {
        var m = new FakeSparseMemory();
        m.WriteBytes(0x9000, new byte[] { 0xAA, 0xBB, 0xCC });
        Assert.Equal((byte)0xAA, m.U8(0x9000));
        Assert.Equal((byte)0xBB, m.U8(0x9001));
        Assert.Equal((byte)0xCC, m.U8(0x9002));
        Assert.Contains(0x9000L, m.WriteOrder);
    }

    [Fact]
    public void WriteBytes_applies_into_a_containing_TerrainBlock_when_one_covers_the_range()
    {
        // Mirrors TryReadBytes's own containment check (base <= addr && addr+len <= base+block.Length)
        // so a treasure-tile-style block write is observable through TryReadBytes too, not just U8.
        var m = new FakeSparseMemory();
        var block = new byte[8];
        m.TerrainBlocks[0xA000] = block;
        m.WriteBytes(0xA002, new byte[] { 0x11, 0x22 });
        Assert.True(m.TryReadBytes(0xA002, 2, out var buf));
        Assert.Equal(new byte[] { 0x11, 0x22 }, buf);
        Assert.Equal((byte)0x11, block[2]);
        Assert.Equal((byte)0x22, block[3]);
    }

    [Fact]
    public void W16_and_WriteBytes_join_W8_in_one_unified_WriteOrder()
    {
        var m = new FakeSparseMemory();
        m.W8(0xB000, 1);
        m.W16(0xB002, 2);
        m.WriteBytes(0xB004, new byte[] { 3 });
        Assert.Equal(new long[] { 0xB000, 0xB002, 0xB004 }, m.WriteOrder);
    }
}
