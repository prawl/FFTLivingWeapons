using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Dragon Rod's "Wyrmblood" signature. At +3, at each of the wielder's turn edges
/// (TurnTracker), the wielder AND every ALLY within 1 Manhattan tile regenerate their OWN
/// maxHP/8 (the vanilla Regen rate), clamped at full. EMULATED regen -- the Regen status bit
/// is unmapped and never touched. Enemies are never healed (positive ally-fingerprint match,
/// not "not an enemy"); the dead are never healed (no accidental revival).
///
/// Pure jobs in Wyrmblood.Policy.cs:
///   (1) IsActive: gates on regenSplashRadius > 0 AND tier >= AtTier.
///   (2) IsTurnEdge: the primed turn-count diff (no splash on first sight after a reset).
///   (3) RegenAmount: maxHp / WyrmbloodDiv, floor 1.
///   (4) InSplash: Manhattan distance <= radius (Ricochet tile math; the wielder itself
///       is distance 0 and so always inside).
/// Heal clamping reuses LifeSap.NewHp (clamp at max, never revive).
/// </summary>
public class WyrmbloodTests
{
    private static WeaponSignature WyrmSig(int radius = 1, int atTier = 3) =>
        new() { AtTier = atTier, RegenSplashRadius = radius, DisplayLabel = "Wyrmblood" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Wyrmblood.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_radius_zero()
        => Assert.False(Wyrmblood.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Wyrmblood.IsActive(WyrmSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Wyrmblood.IsActive(WyrmSig(), tier: 3));
        Assert.True(Wyrmblood.IsActive(WyrmSig(), tier: 4));
    }

    // ---- (2) IsTurnEdge: primed turn-count diff ----

    [Theory]
    [InlineData(-1, 0, false)]   // unprimed: first sight baselines silently
    [InlineData(-1, 3, false)]   // unprimed mid-battle (re-equip): baseline, no splash
    [InlineData(0, 1, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]    // no new turn
    [InlineData(3, 2, false)]    // tracker reset under us -> re-baseline, no splash
    public void IsTurnEdge_fires_only_on_a_primed_increment(int last, int now, bool expected)
        => Assert.Equal(expected, Wyrmblood.IsTurnEdge(last, now));

    // ---- (3) RegenAmount: maxHp/8 (vanilla regen), floor 1 ----

    [Theory]
    [InlineData(160, 20)]
    [InlineData(100, 12)]   // integer floor: 100/8 = 12
    [InlineData(80, 10)]
    [InlineData(7, 1)]      // floor 1 on tiny units
    [InlineData(0, 0)]      // junk maxHp -> no heal
    public void RegenAmount_is_an_eighth_of_max_floor_1(int maxHp, int expected)
        => Assert.Equal(expected, Wyrmblood.RegenAmount(maxHp, 8));

    // ---- (4) InSplash: Manhattan <= radius ----

    [Theory]
    [InlineData(5, 5, 5, 5, true)]    // the wielder itself (distance 0)
    [InlineData(5, 5, 5, 6, true)]    // orthogonally adjacent
    [InlineData(5, 5, 4, 5, true)]
    [InlineData(5, 5, 6, 6, false)]   // diagonal = Manhattan 2, outside radius 1
    [InlineData(5, 5, 7, 5, false)]   // two tiles away
    public void InSplash_uses_manhattan_distance(int wx, int wy, int x, int y, bool expected)
        => Assert.Equal(expected, Wyrmblood.InSplash(wx, wy, x, y, radius: 1));

    // ---- heal clamping is shared with Life Sap (clamp at max, never revive) ----

    [Fact]
    public void Splash_heal_clamps_and_never_revives_via_LifeSap_NewHp()
    {
        Assert.Equal(100, LifeSap.NewHp(95, 100, Wyrmblood.RegenAmount(100, 8)));   // clamped
        Assert.Equal(0, LifeSap.NewHp(0, 100, Wyrmblood.RegenAmount(100, 8)));      // dead stays dead
        Assert.Equal(62, LifeSap.NewHp(50, 100, Wyrmblood.RegenAmount(100, 8)));
    }

    // ---- Main-hand-only activation contract (B2) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.
    // Wyrmblood resolves wielder via Wielder.TryResolveMainHand (RRHand-only match).

    [Fact]
    public void ActivatesOnMainHandOnly_is_documented_in_policy()
        => Assert.True(Wyrmblood.ActivatesOnMainHandOnly);

    // ---- LW-153 pulse pins: the STATEFUL heal loop, driven directly (internal for test
    // reach). Mirrors RenewalTests' pulse pins; written GREEN against the pre-fold
    // Wyrmblood.Splash, so the shared HealPulse core must keep them green. ----

    private static readonly (int mhp, int lvl, int br, int fa) AllyFp = (300, 40, 60, 55);

    private static FakeSparseMemory PulseRig(out Wyrmblood w)
    {
        var m = new FakeSparseMemory();
        BandFixtures.SeedAllyFpAt(m, idx: 1, mhp: AllyFp.mhp, lvl: AllyFp.lvl, br: AllyFp.br, fa: AllyFp.fa);
        w = new Wyrmblood(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), new TurnTracker(m), m);
        return m;
    }

    private static long SeatAt(FakeSparseMemory m, int slot, int gx, int gy, int hp,
                               (int mhp, int lvl, int br, int fa)? fp = null)
    {
        var f = fp ?? AllyFp;
        MemSeats.SeatBand(m, slot, weapon: 0, lvl: f.lvl, br: f.br, fa: f.fa, gx: gx, gy: gy, hp: hp, maxHp: f.mhp);
        long e = Band.Entry(slot);
        m.MarkWritable(e + Offsets.AHp, 2);   // production writes n=2 (BandHeal.cs WriteHp)
        return e;
    }

    private static int ExpectedHealed(int hp) =>
        BandHeal.NewHp(hp, AllyFp.mhp, Wyrmblood.RegenAmount(AllyFp.mhp, Tuning.WyrmbloodDiv));

    [Fact]
    public void Splash_heals_a_live_ally_in_radius()
    {
        var m = PulseRig(out var w);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 200);
        w.Splash(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)ExpectedHealed(200), m.U16s[e + Offsets.AHp]);
        Assert.NotEqual(200, ExpectedHealed(200));   // non-vacuity: the heal really moves HP
    }

    [Fact]
    public void Splash_never_heals_a_non_ally_fingerprint()
    {
        var m = PulseRig(out var w);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 200, fp: (400, 30, 45, 45));
        w.Splash(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)200, m.U16s[e + Offsets.AHp]);   // untouched
    }

    [Fact]
    public void Splash_never_revives_a_dead_ally()
    {
        var m = PulseRig(out var w);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 0);
        w.Splash(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)0, m.U16s[e + Offsets.AHp]);     // dead stays dead
    }

    [Fact]
    public void Splash_skips_the_diagonal_neighbor_Manhattan()
    {
        // The mirror of RenewalTests' Chebyshev pin, SAME geometry: at radius 1 the diagonal
        // tile is Manhattan distance 2, so the splash must NOT reach it. Together the two pins
        // hold the twins' one deliberate metric difference through the shared-core fold.
        var m = PulseRig(out var w);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 6, hp: 200);
        w.Splash(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)200, m.U16s[e + Offsets.AHp]);   // out of Manhattan range
    }
}
