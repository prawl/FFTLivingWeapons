using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-398: the pure shape reader behind the band and static array doors. Byte arrays in, parsed units out.
/// </summary>
public class BattleRegionShapeTests
{
    internal static byte[] EmptyBand() => new byte[Offsets.BandSlots * Offsets.CombatStride];

    internal static byte[] EmptyArray() => new byte[Offsets.NSlots * Offsets.ArrayStride];

    internal static void PutBand(byte[] buf, int seat, int lvl, int br, int fa, int mhp, int hp,
        int nameId = 0, int gx = 4, int gy = 5)
    {
        int e = seat * Offsets.CombatStride + Offsets.BandEntry;
        buf[e + Offsets.ALevel] = (byte)lvl;
        buf[e + Offsets.ABrave] = (byte)br;
        buf[e + Offsets.AFaith] = (byte)fa;
        buf[e + Offsets.AHp] = (byte)hp; buf[e + Offsets.AHp + 1] = (byte)(hp >> 8);
        buf[e + Offsets.AMaxHp] = (byte)mhp; buf[e + Offsets.AMaxHp + 1] = (byte)(mhp >> 8);
        buf[e + Offsets.AGx] = (byte)gx;
        buf[e + Offsets.AGy] = (byte)gy;
        buf[e + Offsets.ANameId] = (byte)nameId; buf[e + Offsets.ANameId + 1] = (byte)(nameId >> 8);
    }

    internal static void PutArray(byte[] buf, int slot, int mhp, int lvl, int br, int fa)
    {
        int e = slot * Offsets.ArrayStride;
        buf[e + Offsets.ALevel] = (byte)lvl;
        buf[e + Offsets.ABrave] = (byte)br;
        buf[e + Offsets.AFaith] = (byte)fa;
        buf[e + Offsets.AMaxHp] = (byte)mhp; buf[e + Offsets.AMaxHp + 1] = (byte)(mhp >> 8);
    }

    private static int BandCount(int lvl = 12, int br = 70, int fa = 65, int mhp = 300, int gx = 4, int gy = 5)
    {
        var buf = EmptyBand();
        PutBand(buf, 3, lvl, br, fa, mhp, 100, gx: gx, gy: gy);
        return BattleRegionShape.ParseBand(buf).Count;
    }

    [Fact]
    public void ParseBand_keeps_an_entry_inside_every_bound_and_reads_its_fields()
    {
        var buf = EmptyBand();
        PutBand(buf, 7, 12, 70, 65, 300, 250, nameId: 1, gx: 4, gy: 5);

        var unit = Assert.Single(BattleRegionShape.ParseBand(buf));

        Assert.Equal((12, 70, 65, 300, 250, 1), (unit.Lvl, unit.Br, unit.Fa, unit.Mhp, unit.Hp, unit.NameId));
        Assert.True(unit.RealPos);
    }

    [Theory]
    [InlineData(0, 70, 65, 300, 4, 5)]
    [InlineData(100, 70, 65, 300, 4, 5)]
    [InlineData(12, 0, 65, 300, 4, 5)]
    [InlineData(12, 101, 65, 300, 4, 5)]
    [InlineData(12, 70, 0, 300, 4, 5)]
    [InlineData(12, 70, 101, 300, 4, 5)]
    [InlineData(12, 70, 65, 0, 4, 5)]
    [InlineData(12, 70, 65, 2000, 4, 5)]
    [InlineData(12, 70, 65, 300, 31, 5)]
    [InlineData(12, 70, 65, 300, 4, 31)]
    public void ParseBand_drops_an_entry_outside_each_bound(int lvl, int br, int fa, int mhp, int gx, int gy)
    {
        Assert.Equal(0, BandCount(lvl, br, fa, mhp, gx, gy));
    }

    [Theory]
    [InlineData(1, 1, 1, 1, 0, 0)]
    [InlineData(99, 100, 100, 1999, 30, 30)]
    public void ParseBand_keeps_the_edges_of_every_bound(int lvl, int br, int fa, int mhp, int gx, int gy)
    {
        Assert.Equal(1, BandCount(lvl, br, fa, mhp, gx, gy));
    }

    [Fact]
    public void ParseBand_reports_a_corner_tile_unit_as_not_real_position()
    {
        var buf = EmptyBand();
        PutBand(buf, 0, 12, 70, 65, 300, 300, gx: 0, gy: 0);

        Assert.False(Assert.Single(BattleRegionShape.ParseBand(buf)).RealPos);
    }

    [Fact]
    public void ParseBand_of_an_all_zero_buffer_is_empty_and_a_short_buffer_parses_what_fits()
    {
        Assert.Empty(BattleRegionShape.ParseBand(EmptyBand()));

        var full = EmptyBand();
        PutBand(full, 0, 12, 70, 65, 300, 300);
        PutBand(full, 2, 13, 71, 66, 301, 301);
        var shortBuf = new byte[Offsets.CombatStride * 2 + 10];
        System.Array.Copy(full, shortBuf, shortBuf.Length);

        Assert.Single(BattleRegionShape.ParseBand(shortBuf));
        Assert.Empty(BattleRegionShape.ParseBand(new byte[5]));
        Assert.Empty(BattleRegionShape.ParseBand(System.Array.Empty<byte>()));
    }

    [Fact]
    public void ParseArray_keeps_mhp_2000_inclusive_and_drops_2001()
    {
        var buf = EmptyArray();
        PutArray(buf, 4, 2000, 12, 70, 65);
        PutArray(buf, 5, 2001, 12, 70, 65);

        var tuple = Assert.Single(BattleRegionShape.ParseArray(buf));

        Assert.Equal((2000, 12, 70, 65), tuple);
    }

    [Fact]
    public void ParseArray_lets_brave_zero_pass_and_drops_level_zero_and_over_100_stats()
    {
        var buf = EmptyArray();
        PutArray(buf, 0, 300, 12, 0, 0);
        PutArray(buf, 1, 300, 0, 70, 65);
        PutArray(buf, 2, 300, 100, 70, 65);
        PutArray(buf, 3, 300, 12, 101, 65);
        PutArray(buf, 4, 300, 12, 70, 101);

        Assert.Equal((300, 12, 0, 0), Assert.Single(BattleRegionShape.ParseArray(buf)));
    }

    [Fact]
    public void ParseArray_drops_the_four_status_byte_slot_of_the_vanilla_image()
    {
        var buf = EmptyArray();
        int e = 25 * Offsets.ArrayStride + 0x48;
        for (int i = 0; i < 4; i++) buf[e + i] = 0xFF;

        Assert.Empty(BattleRegionShape.ParseArray(buf));
        Assert.Empty(BattleRegionShape.ParseArray(new byte[3]));
    }

    private static (int lvl, int br, int fa, int nameId) Row(int lvl = 12, int br = 70, int fa = 65, int nameId = 1)
        => (lvl, br, fa, nameId);

    private static List<BattleRegionShape.Unit> BandOf(int lvl, int br, int fa, int nameId)
    {
        var buf = EmptyBand();
        PutBand(buf, 5, lvl, br, fa, 300, 300, nameId);
        return BattleRegionShape.ParseBand(buf);
    }

    [Fact]
    public void MirrorsRoster_matches_tier_one_on_name_id_and_stats()
    {
        Assert.True(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 65, 1), new[] { Row() }));
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(12, 71, 65, 1), new[] { Row() }));
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 66, 1), new[] { Row() }));
    }

    [Fact]
    public void MirrorsRoster_matches_tier_two_when_the_frame_name_id_reads_zero()
    {
        Assert.True(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 65, 0), new[] { Row() }));
    }

    [Fact]
    public void MirrorsRoster_level_drift_window_is_nine()
    {
        Assert.True(BattleRegionShape.MirrorsRoster(BandOf(20, 70, 65, 1), new[] { Row() }));
        Assert.True(BattleRegionShape.MirrorsRoster(BandOf(21, 70, 65, 1), new[] { Row() }));
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(22, 70, 65, 1), new[] { Row() }));
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(11, 70, 65, 1), new[] { Row() }));
    }

    [Fact]
    public void MirrorsRoster_vetoes_a_foreign_nonzero_name_id_with_matching_stats()
    {
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 65, 9), new[] { Row() }));
    }

    [Fact]
    public void MirrorsRoster_for_a_roster_name_id_of_zero_matches_only_an_entry_name_id_of_zero()
    {
        var rows = new[] { Row(nameId: 0) };

        Assert.True(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 65, 0), rows));
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 65, 1), rows));
    }

    [Fact]
    public void MirrorsRoster_with_no_rows_or_no_entries_is_false()
    {
        Assert.False(BattleRegionShape.MirrorsRoster(BandOf(12, 70, 65, 1), System.Array.Empty<(int, int, int, int)>()));
        Assert.False(BattleRegionShape.MirrorsRoster(new List<BattleRegionShape.Unit>(), new[] { Row() }));
    }

    [Fact]
    public void SharesUnit_needs_one_identical_tuple()
    {
        var band = BandOf(12, 70, 65, 1);

        Assert.True(BattleRegionShape.SharesUnit(new List<(int, int, int, int)> { (9, 9, 9, 9), (300, 12, 70, 65) }, band));
        Assert.False(BattleRegionShape.SharesUnit(new List<(int, int, int, int)> { (301, 12, 70, 65) }, band));
        Assert.False(BattleRegionShape.SharesUnit(new List<(int, int, int, int)> { (300, 12, 70, 65) },
            new List<BattleRegionShape.Unit>()));
    }
}
