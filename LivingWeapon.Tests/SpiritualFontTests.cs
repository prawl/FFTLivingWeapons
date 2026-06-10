using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Wellspring Rod's "Spiritual Font" signature, REWORKED: the runtime restores HP AND MP itself
/// at the +3 wielder's completed-turn edge (their OWN scheduler CT, CtTurns) IF the wielder's
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

    // ---- (8) the corpse gate: a dead wielder gains NOTHING. The HP half already no-ops via
    //      LifeSap.NewHp; the MP half must skip a corpse too (moved, then died before the
    //      turn edge -- trap tile, counter-kill). mpOk alone is NOT enough. ----

    [Theory]
    [InlineData(0, true, false)]    // dead + proven layout: still no MP into a corpse
    [InlineData(0, false, false)]   // dead + unproven layout
    [InlineData(1, true, true)]     // barely alive + proven layout: the MP half runs
    [InlineData(40, false, false)]  // alive but unproven layout: HP-only this battle
    public void Mp_half_requires_a_living_wielder_and_a_proven_layout(int hp, bool mpOk, bool expected)
        => Assert.Equal(expected, SpiritualFont.MpHalfAllowed(hp, mpOk));

    // ---- (9) the turn edge: the wielder's OWN scheduler CT (band +0x25, CtTurns pull-down) --
    //      NOT the global acted-edge TurnTracker, whose cursor-following active-struct
    //      attribution mis-credited turns live (it stalled Rapture's expiry the same way). ----

    [Fact]
    public void Turn_edge_is_the_wielders_own_ct_pull_down()
    {
        var t = new CtTurns();
        t.Observe(95);                  // the turn came (>= TurnHi)...
        Assert.Equal(0, t.Completed);
        t.Observe(10);                  // ...and was taken (< TurnLo): one completed turn
        Assert.Equal(1, t.Completed);
        t.Observe(80); t.Observe(60);   // mid-band drift: neither a rise nor a fall
        Assert.Equal(1, t.Completed);
        t.Observe(91); t.Observe(75);   // rose, but 75 >= TurnLo: not yet completed
        Assert.Equal(1, t.Completed);
        t.Observe(69);                  // an unlocated gap lands the edge late, never lost
        Assert.Equal(2, t.Completed);
    }

    [Fact]
    public void Reequip_and_battle_reset_zero_the_ct_clock()
    {
        var t = new CtTurns();
        t.Observe(95); t.Observe(0);
        Assert.Equal(1, t.Completed);
        t.Reset();
        t.Observe(50);                  // post-reset low CT: no phantom completed turn
        Assert.Equal(0, t.Completed);
    }

    // ---- (10) inLive vs onField gating: CT observation (and everything downstream) must run
    //      on ANY genuine live-battle tick (inLive=true), not only onField ticks. MP layout
    //      validation (ValidateMpLayout) is the only thing gated to onField ticks -- the band
    //      is most coherent there and the check only needs to latch once per battle.
    //      This exercises the same CtTurns contract as (9), confirming the clock ticks regardless
    //      of onField: if inLive=false the CT is never observed and no turns complete; if
    //      inLive=true and onField=false the CT is observed and turns complete normally. ----

    [Fact]
    public void Ct_ticks_accumulate_independently_of_onField()
    {
        // The CT turn-clock is pure (CtTurns); the gating in Tick should NOT suppress it when
        // inLive is true even if onField is false (player dwells on target during their own turn).
        var t = new CtTurns();
        // Simulate: high CT seen (player's action menu open, battleMode 1 = !onField but inLive)
        t.Observe(95);
        Assert.Equal(0, t.Completed);   // not yet -- pull-down hasn't happened
        // Simulate: CT drops (player executed their action, still under battleMode 1)
        t.Observe(12);
        Assert.Equal(1, t.Completed);   // turn completed -- observable without onField
        // A second inLive-but-not-onField cycle should accumulate again
        t.Observe(91); t.Observe(8);
        Assert.Equal(2, t.Completed);
    }
}
