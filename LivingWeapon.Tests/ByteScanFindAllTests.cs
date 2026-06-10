using System.Collections.Generic;
using System.Text;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// ByteScan.FindAll: locates all occurrences of a needle in a buffer within
/// a specified range, respecting lookback/searchable boundaries.
/// </summary>
public class ByteScanFindAllTests
{
    [Fact]
    public void FindAll_empty_needle_no_hits()
    {
        var buf = Encoding.ASCII.GetBytes("Hello world");
        var needle = Array.Empty<byte>();
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 0, buf.Length, hits);

        Assert.Empty(hits);
    }

    [Fact]
    public void FindAll_needle_found_multiple_times()
    {
        var buf = Encoding.ASCII.GetBytes("aaa");
        var needle = Encoding.ASCII.GetBytes("aa");
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 0, buf.Length, hits);

        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0]);
        Assert.Equal(1, hits[1]);
    }

    [Fact]
    public void FindAll_respects_from_boundary()
    {
        var buf = Encoding.ASCII.GetBytes("aaabbb");
        var needle = Encoding.ASCII.GetBytes("a");
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 2, buf.Length, hits);

        Assert.Single(hits);
        Assert.Equal(2, hits[0]);
    }

    [Fact]
    public void FindAll_respects_toExclusive_boundary()
    {
        var buf = Encoding.ASCII.GetBytes("aaabbb");
        var needle = Encoding.ASCII.GetBytes("b");
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 0, 3, hits);

        Assert.Empty(hits);
    }

    [Fact]
    public void FindAll_needle_extends_past_toExclusive()
    {
        var buf = Encoding.ASCII.GetBytes("hello");
        var needle = Encoding.ASCII.GetBytes("llo");
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 0, 3, hits);

        Assert.NotEmpty(hits);
        Assert.Contains(2, hits);
    }

    [Fact]
    public void FindAll_positions_are_where_match_starts()
    {
        var buf = Encoding.ASCII.GetBytes("hello");
        var needle = Encoding.ASCII.GetBytes("lo");
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 0, buf.Length, hits);

        Assert.Single(hits);
        Assert.Equal(3, hits[0]);
    }

    [Fact]
    public void FindAll_no_match_empty_result()
    {
        var buf = Encoding.ASCII.GetBytes("hello");
        var needle = Encoding.ASCII.GetBytes("xyz");
        var hits = new List<int>();

        ByteScan.FindAll(buf, needle, 0, buf.Length, hits);

        Assert.Empty(hits);
    }
}
