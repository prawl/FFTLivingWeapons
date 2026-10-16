using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-388 R12: the ten accessor entry landmarks (ExtendedDonorThunks) are
/// self-consistent and tied to <see cref="Offsets"/>, so a typo in a stolen call's rel32 or an
/// expected-bytes row fails here instead of at the owner's boot.</summary>
public class AccessorEntryTests
{
    private static List<AccessorEntry> Ten()
    {
        var all = new List<AccessorEntry> { ExtendedDonorThunks.WeaponStat };
        all.AddRange(ExtendedDonorThunks.DonorThunks.Select(t => t.Entry));
        return all;
    }

    /// <summary>Row indexes, not entries: the entry type is internal and xUnit theories on a
    /// public class cannot take it as a parameter.</summary>
    public static IEnumerable<object[]> AllTen() => Enumerable.Range(0, 10).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(AllTen))]
    public void Every_entry_steals_at_least_a_jump_and_names_a_well_formed_call(int index)
    {
        var e = Ten()[index];
        Assert.True(e.Expected.Length >= 5, e.Label);
        if (e.CallAt == -1) return;
        Assert.True(e.CallAt >= 0 && e.CallAt + 5 <= e.Expected.Length, e.Label);
        Assert.Equal(0xE8, e.Expected[e.CallAt]);
    }

    [Fact]
    public void The_ten_entries_are_the_ten_distinct_offsets_thunks()
    {
        var all = Ten();
        Assert.Equal(10, all.Count);
        Assert.Equal(10, all.Select(e => e.Addr).Distinct().Count());
        Assert.Equal(Offsets.ThunkWeaponStat, ExtendedDonorThunks.WeaponStat.Addr);
    }

    [Fact]
    public void The_two_call_entries_decode_to_the_range_index_entry()
    {
        var calls = Ten().Where(e => e.CallAt >= 0).ToList();
        Assert.Equal(new[] { "sprite-pair", "range-base" }.OrderBy(x => x), calls.Select(e => e.Label).OrderBy(x => x));
        foreach (var e in calls)
        {
            int rel = System.BitConverter.ToInt32(e.Expected, e.CallAt + 1);
            Assert.Equal(Offsets.ThunkRangeIndex, e.Addr + e.CallAt + 5 + rel);
        }
    }
}
