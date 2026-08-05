using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-149 stage B: Band.TryReadUnit, the shared six-line sanity read (compute the slot's
/// entry address, guard the maxHp read, reject mhp/lvl/br/fa outside their bounds) that
/// Plague/Ricochet/Larceny/Maim/Kobu/Benediction/Puppeteer each duplicated verbatim in their
/// per-tick band walk (Band.Sanity.cs). Two overloads: the no-hp core (used by Plague, which
/// reads no HP in its band walk) and the hp-reading sibling (the other six callers). Bounds
/// mirror the band-entry bound every one of those seven copies shipped with: mhp EXCLUSIVE at
/// 2000 (NOT the static-array fingerprint sweep's inclusive 2000 -- see Band.EnemyFingerprints' own doc
/// for why the two deliberately differ; that bound is untouched by this change), level 1..99,
/// brave/faith 1..100.
/// </summary>
public class BandSanityTests
{
    /// <summary>Sparse fake with per-address touch tracking on U16 reads, so the no-hp
    /// overload's read surface (it must never touch AHp) is independently observable rather
    /// than only inferred from the out-parameter shape.</summary>
    private sealed class TouchFake : IGameMemory
    {
        public readonly Dictionary<long, byte> U8s = new();
        public readonly Dictionary<long, ushort> U16s = new();
        public readonly HashSet<long> ReadableAddrs = new();
        public readonly HashSet<long> TouchedU16 = new();
        public byte U8(long a) => U8s.TryGetValue(a, out var v) ? v : (byte)0;
        public ushort U16(long a) { TouchedU16.Add(a); return U16s.TryGetValue(a, out var v) ? v : (ushort)0; }
        public bool Readable(long a, int n) => ReadableAddrs.Contains(a);
    }

    private static void Seat(TouchFake m, int slot, int mhp, int lvl, int br, int fa, int hp = 100)
    {
        long addr = Band.Entry(slot);
        m.ReadableAddrs.Add(addr + Offsets.AMaxHp);
        m.U16s[addr + Offsets.AMaxHp] = (ushort)mhp;
        m.U8s[addr + Offsets.ALevel] = (byte)lvl;
        m.U8s[addr + Offsets.ABrave] = (byte)br;
        m.U8s[addr + Offsets.AFaith] = (byte)fa;
        m.ReadableAddrs.Add(addr + Offsets.AHp);
        m.U16s[addr + Offsets.AHp] = (ushort)hp;
    }

    [Fact]
    public void TryReadUnit_accepts_a_sane_unit()
    {
        var m = new TouchFake();
        Seat(m, 3, mhp: 200, lvl: 10, br: 50, fa: 50);
        Assert.True(Band.TryReadUnit(m, 3, out long addr, out var fp));
        Assert.Equal(Band.Entry(3), addr);
        Assert.Equal((200, 10, 50, 50), fp);
    }

    [Fact]
    public void TryReadUnit_hp_overload_accepts_a_sane_unit_and_reads_hp()
    {
        var m = new TouchFake();
        Seat(m, 3, mhp: 200, lvl: 10, br: 50, fa: 50, hp: 77);
        Assert.True(Band.TryReadUnit(m, 3, out long addr, out var fp, out int hp));
        Assert.Equal(Band.Entry(3), addr);
        Assert.Equal((200, 10, 50, 50), fp);
        Assert.Equal(77, hp);
    }

    [Fact]
    public void TryReadUnit_rejects_mhp_exactly_2000()
    {
        // THE NON-VACUITY TARGET: loosening the helper's bound to "> 2000" turns this green
        // for the wrong reason.
        var m = new TouchFake();
        Seat(m, 0, mhp: 2000, lvl: 10, br: 50, fa: 50);
        Assert.False(Band.TryReadUnit(m, 0, out _, out _));
    }

    [Fact]
    public void TryReadUnit_accepts_mhp_1999_the_boundary_just_below_the_reject()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 1999, lvl: 10, br: 50, fa: 50);
        Assert.True(Band.TryReadUnit(m, 0, out _, out _));
    }

    [Fact]
    public void TryReadUnit_rejects_mhp_zero()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 0, lvl: 10, br: 50, fa: 50);
        Assert.False(Band.TryReadUnit(m, 0, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void TryReadUnit_rejects_level_out_of_1_to_99(int lvl)
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: lvl, br: 50, fa: 50);
        Assert.False(Band.TryReadUnit(m, 0, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void TryReadUnit_rejects_brave_out_of_1_to_100(int br)
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: 10, br: br, fa: 50);
        Assert.False(Band.TryReadUnit(m, 0, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void TryReadUnit_rejects_faith_out_of_1_to_100(int fa)
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: 10, br: 50, fa: fa);
        Assert.False(Band.TryReadUnit(m, 0, out _, out _));
    }

    [Fact]
    public void TryReadUnit_accepts_brave_and_faith_at_exactly_100()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: 10, br: 100, fa: 100);
        Assert.True(Band.TryReadUnit(m, 0, out _, out var fp));
        Assert.Equal((200, 10, 100, 100), fp);
    }

    [Fact]
    public void TryReadUnit_returns_false_when_AMaxHp_unreadable()
    {
        var m = new TouchFake();   // nothing marked Readable
        Assert.False(Band.TryReadUnit(m, 0, out long addr, out var fp));
        Assert.Equal(Band.Entry(0), addr);   // addr is still the slot's entry, even on refusal
        Assert.Equal((0, 0, 0, 0), fp);
    }

    [Fact]
    public void HpOverload_fails_safe_to_zero_hp_when_AHp_itself_unreadable()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: 10, br: 50, fa: 50, hp: 77);
        m.ReadableAddrs.Remove(Band.Entry(0) + Offsets.AHp);   // sane fingerprint, but AHp unreadable
        Assert.True(Band.TryReadUnit(m, 0, out _, out _, out int hp));
        Assert.Equal(0, hp);
    }

    [Fact]
    public void HpOverload_returns_false_and_zero_hp_when_the_core_sanity_check_fails()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 2000, lvl: 10, br: 50, fa: 50, hp: 77);   // fails the mhp bound
        Assert.False(Band.TryReadUnit(m, 0, out _, out _, out int hp));
        Assert.Equal(0, hp);
    }

    [Fact]
    public void NoHp_overload_never_touches_the_AHp_field()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: 10, br: 50, fa: 50, hp: 77);
        Band.TryReadUnit(m, 0, out long addr, out _);
        Assert.DoesNotContain(addr + Offsets.AHp, m.TouchedU16);
    }

    [Fact]
    public void Hp_overload_does_touch_the_AHp_field()
    {
        var m = new TouchFake();
        Seat(m, 0, mhp: 200, lvl: 10, br: 50, fa: 50, hp: 77);
        Band.TryReadUnit(m, 0, out long addr, out _, out _);
        Assert.Contains(addr + Offsets.AHp, m.TouchedU16);
    }
}
