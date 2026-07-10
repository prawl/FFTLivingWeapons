using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-51 Tier-1: kills.json's pure schema validator. Structurally invalid input (not a JSON
/// object) is a hard failure the caller treats like a corrupt file; a well-formed object with
/// some bad entries is lenient: each malformed row is dropped and counted, never failing the
/// whole parse.
/// </summary>
public class KillsSchemaTests
{
    [Fact]
    public void Valid_object_parses_to_the_full_map_with_zero_dropped()
    {
        bool ok = KillsSchema.TryParse("{\"1\":5,\"80\":51}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(0, dropped);
        Assert.Equal(5, map[1]);
        Assert.Equal(51, map[80]);
        Assert.Equal(2, map.Count);
    }

    [Theory]
    [InlineData("[1,2,3]")]      // array
    [InlineData("\"just text\"")] // scalar string
    [InlineData("42")]           // scalar number
    [InlineData("null")]         // JSON null
    [InlineData("][")]           // garbage / invalid JSON
    [InlineData("{ not json")]   // truncated / invalid JSON
    public void Non_object_json_returns_false(string json)
    {
        bool ok = KillsSchema.TryParse(json, out var map, out int dropped);

        Assert.False(ok);
    }

    [Fact]
    public void Non_numeric_key_is_dropped_and_counted()
    {
        bool ok = KillsSchema.TryParse("{\"9\":3,\"junk\":5}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(1, dropped);
        Assert.Equal(3, map[9]);
        Assert.Single(map);
    }

    [Fact]
    public void Negative_key_is_dropped_and_counted()
    {
        bool ok = KillsSchema.TryParse("{\"-1\":3,\"9\":7}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(1, dropped);
        Assert.Equal(7, map[9]);
        Assert.Single(map);
    }

    [Fact]
    public void Negative_value_is_dropped_and_counted()
    {
        bool ok = KillsSchema.TryParse("{\"9\":-3,\"10\":4}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(1, dropped);
        Assert.Equal(4, map[10]);
        Assert.Single(map);
    }

    [Fact]
    public void Non_integer_value_is_dropped_and_counted()
    {
        bool ok = KillsSchema.TryParse("{\"9\":3.5,\"10\":4}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(1, dropped);
        Assert.Equal(4, map[10]);
        Assert.Single(map);
    }

    [Fact]
    public void Non_numeric_value_string_is_dropped_and_counted()
    {
        bool ok = KillsSchema.TryParse("{\"9\":\"abc\",\"10\":4}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(1, dropped);
        Assert.Equal(4, map[10]);
        Assert.Single(map);
    }

    [Fact]
    public void Dropped_count_accumulates_across_multiple_bad_entries()
    {
        bool ok = KillsSchema.TryParse("{\"a\":1,\"b\":2,\"9\":3,\"-1\":4}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(3, dropped);
        Assert.Equal(3, map[9]);
        Assert.Single(map);
    }

    [Fact]
    public void Empty_object_parses_to_an_empty_map()
    {
        bool ok = KillsSchema.TryParse("{}", out var map, out int dropped);

        Assert.True(ok);
        Assert.Equal(0, dropped);
        Assert.Empty(map);
    }
}
