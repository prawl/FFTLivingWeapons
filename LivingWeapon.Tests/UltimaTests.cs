using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Ultima policy (UltimaPolicy): HP% band edges, the unreadable-HP safety invariant,
/// PaHeld rounding, the table lock, and the lane-ownership helpers. Mirrors the coverage bar
/// set by AfterimageTests (pure policy only; the memory hold in GrowthEngine.Ultima.cs is
/// exercised by the live-verify step before commit).
/// </summary>
public class UltimaTests
{
    // The locked 4x5 table, duplicated here so a future typo in Tuning.UltimaMul is caught.
    private static readonly int[][] LockedTable =
    {
        new[] { 115, 110, 80, 70, 50 },  // tier 0
        new[] { 120, 113, 83, 73, 53 },  // tier 1
        new[] { 125, 116, 86, 76, 56 },  // tier 2
        new[] { 130, 120, 90, 80, 60 },  // tier 3
    };

    // ── Band() ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Band_FullHp_IsZero()
        => Assert.Equal(0, UltimaPolicy.Band(100, 100));

    [Fact]
    public void Band_AboveMaxHp_IsZero()
        => Assert.Equal(0, UltimaPolicy.Band(110, 100));   // buffed HP over max -> top band

    [Fact]
    public void Band_NinetyNinePct_IsOne()
        => Assert.Equal(1, UltimaPolicy.Band(99, 100));

    [Fact]
    public void Band_SeventyFivePct_IsOne()
        => Assert.Equal(1, UltimaPolicy.Band(75, 100));

    [Fact]
    public void Band_SeventyFourPct_IsTwo()
        => Assert.Equal(2, UltimaPolicy.Band(74, 100));

    [Fact]
    public void Band_FiftyPct_IsTwo()
        => Assert.Equal(2, UltimaPolicy.Band(50, 100));

    [Fact]
    public void Band_FortyNinePct_IsThree()
        => Assert.Equal(3, UltimaPolicy.Band(49, 100));

    [Fact]
    public void Band_TwentyFivePct_IsThree()
        => Assert.Equal(3, UltimaPolicy.Band(25, 100));

    [Fact]
    public void Band_TwentyFourPct_IsFour()
        => Assert.Equal(4, UltimaPolicy.Band(24, 100));

    [Fact]
    public void Band_OnePct_IsFour()
        => Assert.Equal(4, UltimaPolicy.Band(1, 100));

    // ── Unreadable / dead HP -> band -1 ────────────────────────────────────────

    [Fact]
    public void Band_ZeroMaxHp_IsMinusOne()
        => Assert.Equal(-1, UltimaPolicy.Band(50, 0));

    [Fact]
    public void Band_ZeroHp_IsMinusOne()
        => Assert.Equal(-1, UltimaPolicy.Band(0, 100));

    [Fact]
    public void PaHeld_UnreadableMaxHp_ReturnsNatural()
        => Assert.Equal(15, UltimaPolicy.PaHeld(15, hp: 50, maxHp: 0, tier: 0, LockedTable));

    [Fact]
    public void PaHeld_ZeroHp_ReturnsNatural()
        => Assert.Equal(15, UltimaPolicy.PaHeld(15, hp: 0, maxHp: 100, tier: 0, LockedTable));

    // ── PaHeld rounding (AwayFromZero, NOT banker's) ───────────────────────────

    [Fact]
    public void PaHeld_FullHp_Tier0_Pct115_Rounds()
    {
        // natural 12, band 0 (100/100), tier 0, pct=115 -> 12*115/100 = 13.8 -> round = 14
        Assert.Equal(14, UltimaPolicy.PaHeld(12, hp: 100, maxHp: 100, tier: 0, LockedTable));
    }

    [Fact]
    public void PaHeld_LowHp_Tier0_Pct50()
    {
        // natural 12, band 4 (10/100), tier 0, pct=50 -> 12*50/100 = 6.0 -> 6
        Assert.Equal(6, UltimaPolicy.PaHeld(12, hp: 10, maxHp: 100, tier: 0, LockedTable));
    }

    [Fact]
    public void PaHeld_MidHp_Tier3_Pct90_Rounds()
    {
        // natural 13, band 2 (50/100), tier 3, pct=90 -> 13*90/100 = 11.7 -> round = 12
        Assert.Equal(12, UltimaPolicy.PaHeld(13, hp: 50, maxHp: 100, tier: 3, LockedTable));
    }

    // ── Tier raises the whole curve ─────────────────────────────────────────────

    [Fact]
    public void PaHeld_Tier3_HigherThanTier0_AtLowHpBand()
    {
        // band 4 (<25%): tier0=50%, tier3=60% -> tier3 always yields more PA
        int tier0 = UltimaPolicy.PaHeld(20, hp: 1, maxHp: 100, tier: 0, LockedTable);
        int tier3 = UltimaPolicy.PaHeld(20, hp: 1, maxHp: 100, tier: 3, LockedTable);
        Assert.True(tier3 > tier0, $"Expected tier3 ({tier3}) > tier0 ({tier0}) at <25% HP band");
    }

    // ── Full table lock ─────────────────────────────────────────────────────────

    [Fact]
    public void UltimaMul_MatchesLockedTable()
    {
        Assert.Equal(4, Tuning.UltimaMul.Length);
        for (int t = 0; t < 4; t++)
        {
            Assert.Equal(5, Tuning.UltimaMul[t].Length);
            for (int b = 0; b < 5; b++)
                Assert.Equal(LockedTable[t][b], Tuning.UltimaMul[t][b]);
        }
    }

    // ── IsUltima / OwnsPa ───────────────────────────────────────────────────────

    [Fact]
    public void IsUltima_TrueOnlyWhenFlagSet()
    {
        Assert.True(UltimaPolicy.IsUltima(new WeaponSignature { Ultima = true }));
        Assert.False(UltimaPolicy.IsUltima(new WeaponSignature { Ultima = false }));
        Assert.False(UltimaPolicy.IsUltima(new WeaponSignature()));
        Assert.False(UltimaPolicy.IsUltima(null));
    }

    [Fact]
    public void OwnsPa_TrueOnlyWhenUltimaFlagSet()
    {
        var mYes = new WeaponMeta { Signature = new WeaponSignature { Ultima = true } };
        var mNo  = new WeaponMeta { Signature = new WeaponSignature { Ultima = false } };
        var mNull = new WeaponMeta { Signature = null };
        Assert.True(GrowthEngine.OwnsPa(mYes));
        Assert.False(GrowthEngine.OwnsPa(mNo));
        Assert.False(GrowthEngine.OwnsPa(mNull));
    }
}
