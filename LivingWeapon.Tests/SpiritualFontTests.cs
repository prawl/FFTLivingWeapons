using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Wellspring Rod's "Spiritual Font" signature, REWORKED: the runtime restores HP AND MP itself
/// at the +3 wielder's completed-turn edge (TurnTracker, Wyrmblood's edge) IF the wielder's
/// grid position changed since their previous turn edge. No engine movement passives -- the
/// live test proved both font bits hold but the engine honors exactly ONE movement passive
/// (it picked Lifefont; only HP ticked on move), so the bit grant is gone.
///
/// Pure jobs in SpiritualFont.Policy.cs:
///   (1) IsActive: gates on fontOnMove AND tier >= AtTier.
///   (2) ShouldFire: position changed since the SNAPSHOTTED previous turn edge; the first
///       edge of a battle (or after a re-equip) only snapshots, never fires.
///   (3) HP half: reuses LifeSap.HealAmount (round, floor 1) + LifeSap.NewHp (clamp at max,
///       never revive -- hp==0 skips).
///   (4) MP half: NewMp clamps at maxMp; mp 0 still gains (0 MP is not death).
///   (5) MpLayoutOk: the PURE per-battle validation gating every MP write -- the +0x18/+0x1A
///       offsets are PROVISIONAL (never live-verified), so a failed sanity sweep over the
///       band means HP-only for that battle.
///   (6) WriteMp: guarded little-endian u16 write, neighbors untouched.
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

    // ---- (2) ShouldFire: moved since the previous turn edge ----

    [Theory]
    [InlineData(false, 5, 5, 7, 8, false)]   // first edge of a battle: snapshot only, never fire
    [InlineData(false, 0, 0, 3, 3, false)]   // unprimed after a re-equip: baseline silently
    [InlineData(true, 5, 5, 5, 5, false)]    // stood still -> no font
    [InlineData(true, 5, 5, 6, 5, true)]     // gx changed
    [InlineData(true, 5, 5, 5, 4, true)]     // gy changed
    [InlineData(true, 5, 5, 9, 1, true)]     // both changed
    public void ShouldFire_only_after_a_snapshotted_position_changed(
        bool posKnown, int lastGx, int lastGy, int gx, int gy, bool expected)
        => Assert.Equal(expected, SpiritualFont.ShouldFire(posKnown, lastGx, lastGy, gx, gy));

    // ---- (3) HP half: round(maxHP * FontHpPct) floor 1, clamp at max, NEVER revive ----

    [Fact]
    public void Hp_half_reuses_LifeSap_round_floor_clamp_and_never_revives()
    {
        Assert.Equal(10, LifeSap.HealAmount(100, Tuning.FontHpPct));
        Assert.Equal(1, LifeSap.HealAmount(7, Tuning.FontHpPct));     // floor 1 on tiny units
        Assert.Equal(60, LifeSap.NewHp(50, 100, LifeSap.HealAmount(100, Tuning.FontHpPct)));
        Assert.Equal(100, LifeSap.NewHp(95, 100, 10));                // clamp at max
        Assert.Equal(0, LifeSap.NewHp(0, 100, 10));                   // dead stays dead (no revive)
    }

    // ---- (4) MP half: NewMp clamps; 0 MP is NOT death and still gains ----

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

    // ---- (5) MpLayoutOk: the PURE per-battle gate on the PROVISIONAL +0x18/+0x1A pair ----
    // Pass needs: >= 2 sampled units, mp <= maxMp AND maxMp <= 999 for ALL of them, and
    // maxMp >= 1 for at least one. Anything else -> HP-only for the battle.

    public static TheoryData<(int mp, int maxMp)[], bool> LayoutCases => new()
    {
        { new[] { (10, 50), (0, 30) }, true },          // sane party
        { new[] { (999, 999), (0, 0) }, true },         // boundary maxMp 999 + one zero-pool unit
        { new[] { (0, 0), (12, 40), (40, 40) }, true }, // a zero-MP unit doesn't fail the sweep
        { new[] { (31, 30), (10, 50) }, false },        // mp > maxMp on ANY unit -> garbage layout
        { new[] { (10, 1000), (10, 50) }, false },      // maxMp > 999 on ANY unit -> garbage layout
        { new[] { (0, 0), (0, 0) }, false },            // nobody has an MP pool -> can't trust the pair
        { new[] { (10, 50) }, false },                  // fewer than 2 units -> not validatable
        { new (int, int)[0], false },                   // empty band -> not validatable
    };

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void MpLayoutOk_requires_two_sane_units_with_a_real_pool(
        (int mp, int maxMp)[] units, bool expected)
        => Assert.Equal(expected, SpiritualFont.MpLayoutOk(units));

    // ---- (6) WriteMp: guarded little-endian u16 at +0x18, neighbors untouched ----

    [Fact]
    public void WriteMp_writes_little_endian_and_preserves_neighbors()
    {
        var buf = new byte[64];
        buf[Offsets.AMp - 1] = 0x77;       // byte before the MP pair
        buf[Offsets.AMaxMp] = 0x99;        // maxMp low byte right after -- must not be touched
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

    // ---- (7) knobs + the provisional offsets, pinned so a retune is deliberate ----

    [Fact]
    public void Font_knobs_are_ten_percent_each()
    {
        Assert.Equal(0.10, Tuning.FontHpPct);
        Assert.Equal(0.10, Tuning.FontMpPct);
    }

    [Fact]
    public void Mp_offsets_are_the_u16_pair_right_after_the_hp_pair()
    {
        Assert.Equal(Offsets.AMaxHp + 2, Offsets.AMp);    // 0x18: provisional, never live-verified
        Assert.Equal(Offsets.AMp + 2, Offsets.AMaxMp);    // 0x1A
    }

    // ---- the turn edge itself is Wyrmblood's shared IsTurnEdge (tested in WyrmbloodTests) ----

    [Fact]
    public void Turn_edge_semantics_are_shared_with_Wyrmblood()
    {
        Assert.False(Wyrmblood.IsTurnEdge(-1, 5));   // unprimed first sight baselines silently
        Assert.True(Wyrmblood.IsTurnEdge(1, 2));
    }
}
