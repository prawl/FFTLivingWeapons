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
}
