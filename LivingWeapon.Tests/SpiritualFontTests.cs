using System.Runtime.InteropServices;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Umbral Rod's "Spiritual Font" signature (moved from Wellspring Rod id 51, tier 1 -- T1 weapons
/// carry no signatures): the runtime restores HP AND MP itself when the actor latch OPENS with
/// weapon id 56 (a rising edge: 56 absent last tick, present now) AND the wielder's grid position
/// changed since their previous action edge. No CT reads -- live testing proved both CT bytes
/// (+0x25 and +0x09) never reached the >=90 threshold across full player turns; the actor latch
/// (KillTracker.LastPlayerWeapons) is the only reliable edge.
///
/// KNOWN GAP: a move-only turn (unit moves, never acts) raises no latch; its movement is credited
/// at the wielder's NEXT action instead (the delta accumulates) -- late, never lost.
///
/// Pure jobs in SpiritualFont.Policy.cs:
///   (1) IsActive: gates on fontOnMove AND tier >= AtTier.
///   (2) ShouldFire: position changed since the SNAPSHOTTED previous action edge; the first
///       edge of a battle (or after a re-equip) only snapshots, never fires.
///   (3) HP half: reuses LifeSap.HealAmount (round, floor 1) + LifeSap.NewHp (clamp at max,
///       never revive -- hp==0 skips).
///   (4) MP half: NewMp clamps at maxMp; mp 0 still gains (0 MP is not death).
///   (5) MpLayoutOk: the PURE per-battle validation gating every MP write -- the +0x18/+0x1A
///       offsets are live-verified 2026-06-10; the guard sweep remains as a per-battle sanity
///       check.
///   (6) WriteMp: guarded little-endian u16 write, neighbors untouched.
///   (7) IsLatchEdge: rising edge on the actor latch (56 absent -> present).
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

    // ---- (2) ShouldFire: moved since the previous action edge ----

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

    // ---- (5) MpLayoutOk: the PURE per-battle gate on the +0x18/+0x1A pair (live-verified 2026-06-10) ----

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

    // ---- (7) knobs + the verified offsets, pinned so a retune is deliberate ----

    [Fact]
    public void Font_knobs_are_ten_percent_each()
    {
        Assert.Equal(0.10, Tuning.FontHpPct);
        Assert.Equal(0.10, Tuning.FontMpPct);
    }

    [Fact]
    public void Mp_offsets_are_the_u16_pair_right_after_the_hp_pair()
    {
        Assert.Equal(Offsets.AMaxHp + 2, Offsets.AMp);    // 0x18: live-verified 2026-06-10
        Assert.Equal(Offsets.AMp + 2, Offsets.AMaxMp);    // 0x1A
    }

    // ---- (8) the corpse gate: a dead wielder gains NOTHING. The HP half already no-ops via
    //      LifeSap.NewHp; the MP half must skip a corpse too (moved, then died before the
    //      action edge -- trap tile, counter-kill). mpOk alone is NOT enough. ----

    [Theory]
    [InlineData(0, true, false)]    // dead + proven layout: still no MP into a corpse
    [InlineData(0, false, false)]   // dead + unproven layout
    [InlineData(1, true, true)]     // barely alive + proven layout: the MP half runs
    [InlineData(40, false, false)]  // alive but unproven layout: HP-only this battle
    public void Mp_half_requires_a_living_wielder_and_a_proven_layout(int hp, bool mpOk, bool expected)
        => Assert.Equal(expected, SpiritualFont.MpHalfAllowed(hp, mpOk));

    // ---- (9) the action edge: weapon 56 appearing in the actor latch (rising edge) --
    //      NOT CT reads. Live testing proved both CT bytes (+0x25 = ExtraTurn's write
    //      target, +0x09 = ACtTurn) never returned >= 90 across full player turns in the
    //      watcher. The actor latch (KillTracker.LastPlayerWeapons) is the proven signal. ----

    [Theory]
    [InlineData(false, false, false)]   // 56 absent both ticks: no edge
    [InlineData(true,  true,  false)]   // 56 present both ticks: latch was already open
    [InlineData(true,  false, false)]   // 56 disappears: falling edge, not a trigger
    [InlineData(false, true,  true)]    // 56 appears: RISING EDGE -> take the action snapshot
    public void IsLatchEdge_fires_only_on_rising_edge(bool wasIn, bool isIn, bool expected)
        => Assert.Equal(expected, SpiritualFont.IsLatchEdge(wasIn, isIn));

    // ---- (10) KNOWN GAP: move-only turns raise no latch ----
    //      A unit that moves but never opens the action menu (move-only turn) never sets
    //      the acted flag, so KillTracker never latches weapon 56, so no action edge fires.
    //      The position delta accumulates: movement is credited at the NEXT action edge.
    //      Late, but never lost. IsLatchEdge is not tested for this path (it is pure);
    //      the gap is a structural property of the trigger source (the acted flag).

    [Fact]
    public void IsLatchEdge_is_false_when_wasIn_was_already_set()
    {
        // Simulate: wielder held the weapon across two consecutive ticks where the latch shows 51.
        // This is NOT a new edge -- the action already started.
        Assert.False(SpiritualFont.IsLatchEdge(wasIn: true, isIn: true));
    }
}
