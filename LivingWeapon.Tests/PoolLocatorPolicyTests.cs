using System.Collections.Generic;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// PoolLocatorPolicy: pure byte[] pool-region signature test (LW-37). No IGameMemory --
/// everything here operates on a plain buffer, reusing CardScanner.FindKills verbatim (no new
/// byte matching). A region "is pool" when it holds at least one FULLY attributed entry (a
/// "Kills: " hit tied to its owner's flavor); DistinctWeaponCount is how many different weapon
/// ids attributed within the buffer, the signal PoolLocator uses to prefer a true pool (every
/// item) over a transient single-item widget.
/// </summary>
public class PoolLocatorPolicyTests
{
    private static Dictionary<int, WeaponMeta> BuildMeta() => new()
    {
        { 1, new WeaponMeta { Name = "Sword", Flavor = "A sharp blade" } },
        { 2, new WeaponMeta { Name = "Staff", Flavor = "B holy relic" } },
    };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Full_named_entry_two_weapons_is_pool_with_distinct_count_2(int enc)
    {
        var meta = BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[600];
        var (_, killsA, flavorA) = CardFixtures.WriteCardForwardWithName(buf, 0, "Sword", "A sharp blade", enc);
        int gap = 20;
        int nextStart = flavorA + ByteScan.Enc("A sharp blade", enc).Length + gap;
        var (_, killsB, flavorB) = CardFixtures.WriteCardForwardWithName(buf, nextStart, "Staff", "B holy relic", enc);

        var result = PoolLocatorPolicy.Scan(buf, lookback: 0, searchable: buf.Length, pats);

        Assert.True(result.IsPool);
        Assert.Equal(2, result.DistinctWeaponCount);
        Assert.Equal(2, result.Hits.Count);

        // Offset math: SlotAddr sits right after "Kills: ", AnchorAddr is the flavor position --
        // both computed by CardScanner.FindKills, never hardcoded here.
        Assert.Equal(killsA, PoolLocatorPolicy.SlotOffset(result.Hits[0]));
        Assert.Equal(flavorA, PoolLocatorPolicy.AnchorOffset(result.Hits[0]));
        Assert.Equal(killsB, PoolLocatorPolicy.SlotOffset(result.Hits[1]));
        Assert.Equal(flavorB, PoolLocatorPolicy.AnchorOffset(result.Hits[1]));
    }

    [Fact]
    public void Single_weapon_full_named_entry_is_pool_with_distinct_count_1()
    {
        var meta = BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[200];
        CardFixtures.WriteCardForwardWithName(buf, 0, "Sword", "A sharp blade", enc: 1);

        var result = PoolLocatorPolicy.Scan(buf, lookback: 0, searchable: buf.Length, pats);

        Assert.True(result.IsPool);
        Assert.Equal(1, result.DistinctWeaponCount);
    }

    [Fact]
    public void Flavor_and_kills_without_an_adjacent_name_is_not_pool()
    {
        // The REAL transient-descriptor shape (confirmed live 2026-07-08): a weapon's flavor tied
        // to a "Kills: " line, but NO weapon name adjacent. FindKills attributes it, yet the card
        // never re-materializes from it. Dropping these is what keeps the ~26 lower-addressed
        // render copies from beating the writable pool in PoolLocator's distinct-weapon tiebreak.
        var meta = BuildMeta();
        var pats = new CardPatterns(meta);

        var buf = new byte[400];
        CardFixtures.WriteCardForward(buf, 0, "A sharp blade", enc: 1);       // flavor + Kills, NO name
        CardFixtures.WriteCardForward(buf, 200, "B holy relic", enc: 1);

        var result = PoolLocatorPolicy.Scan(buf, lookback: 0, searchable: buf.Length, pats);

        Assert.False(result.IsPool);
        Assert.Equal(0, result.DistinctWeaponCount);
    }

    [Fact]
    public void Partial_entry_no_nearby_flavor_is_not_pool()
    {
        // "Kills: " + a valid meter slot, with NO weapon flavor anywhere near it: the
        // decoy shape a widget or a mid-transition buffer can present.
        var meta = BuildMeta();
        var pats = new CardPatterns(meta);

        var parts = new List<byte>();
        parts.AddRange(ByteScan.Enc("Kills: ", 1));
        parts.AddRange(ByteScan.Enc(Signatures.KillsMeterSlot(0), 1));
        byte[] buf = parts.ToArray();

        var result = PoolLocatorPolicy.Scan(buf, lookback: 0, searchable: buf.Length, pats);

        Assert.False(result.IsPool);
        Assert.Equal(0, result.DistinctWeaponCount);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Empty_buffer_is_not_pool()
    {
        var meta = BuildMeta();
        var pats = new CardPatterns(meta);

        var result = PoolLocatorPolicy.Scan(System.Array.Empty<byte>(), lookback: 0, searchable: 0, pats);

        Assert.False(result.IsPool);
        Assert.Equal(0, result.DistinctWeaponCount);
    }
}
