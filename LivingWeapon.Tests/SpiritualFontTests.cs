using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Umbral Rod's "Spiritual Font" signature -- pure helper tests.
/// MoveWatch state-machine tests live in SpiritualFontMoveWatchTests.cs.
///
/// TRIGGER HISTORY (four failed designs, documented in SpiritualFont.cs):
///   (1) Global TurnTracker acted-edge -- cursor-following struct mis-credited edges.
///   (2) Band +0x25 ACtSlam CT read -- write-only byte; zero transitions observed live.
///   (3) Band +0x09 ACtTurn CT read -- never reached >=90 across full player turns.
///   (4) Actor latch rising edge -- fired for action 1, missed the second action entirely;
///       the global Acted flag pulses unreliably (documented trap family).
/// Position-poll (MoveWatch) needs no engine cooperation.
///
/// Pure helpers in SpiritualFont.Policy.cs:
///   (1) IsActive: gates on fontOnMove AND tier >= AtTier.
///   (2) HP half: LifeSap.HealAmount (round, floor 1) + LifeSap.NewHp (clamp, never revive).
///   (3) MP half: NewMp clamps at maxMp; mp 0 still gains (not death).
///   (4) MpLayoutOk: per-battle gate on the +0x18/+0x1A pair (live-verified 2026-06-10).
///   (5) WriteMp: guarded little-endian u16 write, neighbors untouched.
/// </summary>
public class SpiritualFontTests
{
    private static WeaponSignature FontSig(int atTier = 3) =>
        new() { AtTier = atTier, FontOnMove = true, DisplayLabel = "Spiritual Font" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(SpiritualFont.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_not_a_font_weapon()
        => Assert.False(SpiritualFont.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(SpiritualFont.IsActive(FontSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(SpiritualFont.IsActive(FontSig(), tier: 3));
        Assert.True(SpiritualFont.IsActive(FontSig(), tier: 4));
    }

    // ---- (2) HP half: round(maxHP * FontHpPct) floor 1, clamp at max, NEVER revive ----

    [Fact]
    public void Hp_half_reuses_LifeSap_round_floor_clamp_and_never_revives()
    {
        Assert.Equal(10, LifeSap.HealAmount(100, Tuning.FontHpPct));
        Assert.Equal(1, LifeSap.HealAmount(7, Tuning.FontHpPct));     // floor 1 on tiny units
        Assert.Equal(60, LifeSap.NewHp(50, 100, LifeSap.HealAmount(100, Tuning.FontHpPct)));
        Assert.Equal(100, LifeSap.NewHp(95, 100, 10));                // clamp at max
        Assert.Equal(0, LifeSap.NewHp(0, 100, 10));                   // dead stays dead (no revive)
    }

    // ---- (3) MP half: NewMp clamps; 0 MP is NOT death and still gains ----

    [Theory]
    [InlineData(50, 100, 10, 60)]
    [InlineData(95, 100, 10, 100)]   // clamp at maxMp
    [InlineData(100, 100, 10, 100)]  // already full
    [InlineData(0, 100, 10, 10)]     // mp 0 still gains -- empty pool, not a corpse
    [InlineData(50, 0, 10, 50)]      // junk maxMp -> unchanged
    [InlineData(50, 100, 0, 50)]     // no gain -> unchanged
    public void NewMp_clamps_at_max_and_zero_mp_still_gains(int mp, int maxMp, int gain, int expected)
        => Assert.Equal(expected, SpiritualFont.NewMp(mp, maxMp, gain));

    [Fact]
    public void Mp_gain_rounds_and_floors_like_the_hp_half()
    {
        Assert.Equal(10, LifeSap.HealAmount(96, Tuning.FontMpPct));   // round(9.6) = 10
        Assert.Equal(1, LifeSap.HealAmount(4, Tuning.FontMpPct));     // floor 1
        Assert.Equal(0, LifeSap.HealAmount(0, Tuning.FontMpPct));     // junk maxMp -> no gain
    }

    // ---- (4) MpLayoutOk ----

    public static TheoryData<(int mp, int maxMp)[], bool> LayoutCases => new()
    {
        { new[] { (10, 50), (0, 30) }, true },
        { new[] { (999, 999), (0, 0) }, true },
        { new[] { (0, 0), (12, 40), (40, 40) }, true },
        { new[] { (31, 30), (10, 50) }, false },
        { new[] { (10, 1000), (10, 50) }, false },
        { new[] { (0, 0), (0, 0) }, false },
        { new[] { (10, 50) }, false },
        { new (int, int)[0], false },
    };

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void MpLayoutOk_requires_two_sane_units_with_a_real_pool(
        (int mp, int maxMp)[] units, bool expected)
        => Assert.Equal(expected, SpiritualFont.MpLayoutOk(units));

    // ---- (5) WriteMp: guarded little-endian u16 at +0x18, neighbors untouched ----

    [Fact]
    public void WriteMp_writes_little_endian_and_preserves_neighbors()
    {
        var buf = new byte[64];
        buf[Offsets.AMp - 1] = 0x77;
        buf[Offsets.AMaxMp] = 0x99;
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            long entry = h.AddrOfPinnedObject().ToInt64();
            SpiritualFont.WriteMp(entry, 0x0234);
            Assert.Equal(0x34, buf[Offsets.AMp]);
            Assert.Equal(0x02, buf[Offsets.AMp + 1]);
            Assert.Equal(0x77, buf[Offsets.AMp - 1]);
            Assert.Equal(0x99, buf[Offsets.AMaxMp]);
        }
        finally { h.Free(); }
    }

    // ---- knobs + verified offsets ----

    [Fact]
    public void Font_knobs_are_ten_percent_each()
    {
        Assert.Equal(0.10, Tuning.FontHpPct);
        Assert.Equal(0.10, Tuning.FontMpPct);
    }

    [Fact]
    public void Mp_offsets_are_the_u16_pair_right_after_the_hp_pair()
    {
        Assert.Equal(Offsets.AMaxHp + 2, Offsets.AMp);
        Assert.Equal(Offsets.AMp + 2, Offsets.AMaxMp);
    }

    // ---- corpse gate ----

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(0, false, false)]
    [InlineData(1, true, true)]
    [InlineData(40, false, false)]
    public void Mp_half_requires_a_living_wielder_and_a_proven_layout(int hp, bool mpOk, bool expected)
        => Assert.Equal(expected, SpiritualFont.MpHalfAllowed(hp, mpOk));
}
