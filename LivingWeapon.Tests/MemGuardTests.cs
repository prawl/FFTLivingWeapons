using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-50's Mem-funnel write gate: <see cref="Mem.WritesEnabled"/> defaults true (existing
/// pinned-buffer suites are unaffected) and, when flipped false, every write method becomes a
/// no-op while reads stay live. Every test restores the flag in a finally block: the assembly is
/// serialized (TestLoggingSetup.cs's CollectionBehavior), but the flag is still a shared static.
/// </summary>
public class MemGuardTests
{
    [Fact]
    public void WritesDisabled_W8_leaves_pinned_buffer_unchanged()
    {
        // THE load-bearing non-vacuous test: comment out Mem.W8's "if (!WritesEnabled) return;"
        // gate line and run ONLY this test to prove it goes red, then restore the gate.
        using var buf = PinnedBuf.Of(1);
        buf.Bytes[0] = 0xAB;
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            Mem.W8(buf.Addr, 0xCD);
            Assert.Equal(0xAB, buf.Bytes[0]);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void WritesDisabled_WriteBytes_and_W16_no_op()
    {
        using var buf = PinnedBuf.Of(4);
        buf.Bytes[0] = 1; buf.Bytes[1] = 2; buf.Bytes[2] = 3; buf.Bytes[3] = 4;
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            Mem.WriteBytes(buf.Addr, new byte[] { 9, 9, 9, 9 });
            Mem.W16(buf.Addr, 0xFFFF);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buf.Bytes);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void WritesEnabled_writes_land()
    {
        using var buf = PinnedBuf.Of(2);
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = true;
            Mem.W8(buf.Addr, 0x42);
            Assert.Equal(0x42, buf.Bytes[0]);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Reads_stay_live_while_disabled()
    {
        using var buf = PinnedBuf.Of(1);
        buf.Bytes[0] = 0x7A;
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            Assert.Equal(0x7A, Mem.U8(buf.Addr));
        }
        finally { Mem.WritesEnabled = prev; }
    }
}
