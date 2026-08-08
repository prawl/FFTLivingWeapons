using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Mending Staff's "Renewal" signature. At +3, at each of the wielder's turn edges
/// (TurnTracker), the wielder AND every ALLY within 1 Chebyshev tile are healed for
/// round(maxHP * 10%) of their OWN max HP, clamped at full. EMULATED heal -- a silent
/// band +0x14 HP write; no status icon, no floating number (proven impossible to make
/// visible). Enemies are never healed (positive ally-fingerprint match, not "not an
/// enemy"); the dead are never healed (no accidental revival).
///
/// Pure jobs in Renewal.Policy.cs:
///   (1) IsActive: gates on regenAuraRadius > 0 AND tier >= AtTier.
///   (2) IsTurnEdge: the primed turn-count diff (no heal on first sight after a reset).
///   (3) HealAmount: LifeSap.HealAmount(maxHp, 0.10) -- 10%, round away-from-zero, floor 1.
///   (4) InAura: Chebyshev distance <= radius (max(|dx|,|dy|)); diagonal is distance 1.
///       The wielder itself is distance 0 and always inside.
/// Heal clamping reuses LifeSap.NewHp (clamp at max, never revive).
/// </summary>
public class RenewalTests
{
    private static WeaponSignature RenewalSig(int radius = 1, int atTier = 3) =>
        new() { AtTier = atTier, RegenAuraRadius = radius, DisplayLabel = "Renewal" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(Renewal.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_radius_zero()
        => Assert.False(Renewal.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(Renewal.IsActive(RenewalSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(Renewal.IsActive(RenewalSig(), tier: 3));
        Assert.True(Renewal.IsActive(RenewalSig(), tier: 4));
    }

    // ---- (2) IsTurnEdge: primed turn-count diff ----

    [Theory]
    [InlineData(-1, 0, false)]   // unprimed: first sight baselines silently
    [InlineData(-1, 3, false)]   // unprimed mid-battle (re-equip): baseline, no aura
    [InlineData(0, 1, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]    // no new turn
    [InlineData(3, 2, false)]    // tracker reset under us -> re-baseline, no aura
    public void IsTurnEdge_fires_only_on_a_primed_increment(int last, int now, bool expected)
        => Assert.Equal(expected, Renewal.IsTurnEdge(last, now));

    // ---- (3) HealAmount: round(maxHp * 0.10), floor 1 ----

    [Theory]
    [InlineData(100, 10)]   // 100 * 0.10 = 10
    [InlineData(7, 1)]      // floor 1 on tiny units
    [InlineData(0, 0)]      // junk maxHp -> no heal
    public void HealAmount_is_ten_pct_of_max_floor_1(int maxHp, int expected)
        => Assert.Equal(expected, LifeSap.HealAmount(maxHp, 0.10));

    // ---- (4) InAura: Chebyshev distance <= radius ----
    // THE load-bearing non-vacuous test: diagonal (5,5,6,6) is Chebyshev 1 (TRUE),
    // the exact case a Manhattan metric would score distance 2 and exclude.

    [Theory]
    [InlineData(5, 5, 5, 5, true)]    // the wielder itself (distance 0)
    [InlineData(5, 5, 5, 6, true)]    // orthogonally adjacent
    [InlineData(5, 5, 4, 5, true)]
    [InlineData(5, 5, 6, 6, true)]    // diagonal = Chebyshev 1, inside radius 1 (NOT Manhattan!)
    [InlineData(5, 5, 7, 5, false)]   // two tiles away orthogonally: Chebyshev 2
    [InlineData(5, 5, 7, 7, false)]   // two tiles diagonal: Chebyshev 2
    public void InAura_uses_chebyshev_distance(int wx, int wy, int x, int y, bool expected)
        => Assert.Equal(expected, Renewal.InAura(wx, wy, x, y, radius: 1));

    // ---- heal clamping is shared with Life Sap (clamp at max, never revive) ----

    [Fact]
    public void Aura_heal_clamps_and_never_revives_via_LifeSap_NewHp()
    {
        int amount100 = LifeSap.HealAmount(100, 0.10);   // = 10
        Assert.Equal(100, LifeSap.NewHp(95, 100, amount100));   // clamped at full
        Assert.Equal(0,   LifeSap.NewHp(0,  100, amount100));   // dead stays dead
    }

    // ---- Main-hand-only activation contract ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.
    // Renewal resolves wielder via Wielder.TryResolveMainHand (RRHand-only match).

    [Fact]
    public void ActivatesOnMainHandOnly_is_documented_in_policy()
        => Assert.True(Renewal.ActivatesOnMainHandOnly);

    // ---- LW-153 pulse pins: the STATEFUL heal loop, driven directly (internal for test reach).
    // Written GREEN against the pre-fold Renewal.Aura, so the shared HealPulse core must keep
    // them green -- the pins outrank the fold, not the other way around. ----

    private static readonly (int mhp, int lvl, int br, int fa) AllyFp = (300, 40, 60, 55);

    /// <summary>Stage a band seat at <paramref name="slot"/> carrying AllyFp (or a stranger's
    /// fp), registered (or not) as a static-array PLAYER so Band.AllyFingerprints recognizes
    /// it, with the HP field writable the way production writes it (BandHeal, n=2).</summary>
    private static FakeSparseMemory PulseRig(out Renewal r)
    {
        var m = new FakeSparseMemory();
        BandFixtures.SeedAllyFpAt(m, idx: 1, mhp: AllyFp.mhp, lvl: AllyFp.lvl, br: AllyFp.br, fa: AllyFp.fa);
        r = new Renewal(new Dictionary<int, WeaponMeta>(), new Dictionary<int, int>(), new TurnTracker(m), m);
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
        BandHeal.NewHp(hp, AllyFp.mhp, BandHeal.HealAmount(AllyFp.mhp, Tuning.RenewalPct));

    [Fact]
    public void Aura_heals_a_live_ally_in_radius()
    {
        var m = PulseRig(out var r);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 200);
        r.Aura(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)ExpectedHealed(200), m.U16s[e + Offsets.AHp]);
        Assert.NotEqual(200, ExpectedHealed(200));   // non-vacuity: the heal really moves HP
    }

    [Fact]
    public void Aura_never_heals_a_non_ally_fingerprint()
    {
        // Positive ally match only: a unit whose fingerprint is NOT a static-array PLAYER slot
        // (an enemy, an uncaptured reinforcement) is never healed, even in radius.
        var m = PulseRig(out var r);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 200, fp: (400, 30, 45, 45));
        r.Aura(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)200, m.U16s[e + Offsets.AHp]);   // untouched
    }

    [Fact]
    public void Aura_never_revives_a_dead_ally()
    {
        var m = PulseRig(out var r);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 0);
        r.Aura(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)0, m.U16s[e + Offsets.AHp]);     // dead stays dead
    }

    [Fact]
    public void Aura_heals_a_band_twin_once()
    {
        var m = PulseRig(out var r);
        long first = SeatAt(m, slot: 30, gx: 6, gy: 5, hp: 200);
        long twin  = SeatAt(m, slot: 31, gx: 5, gy: 6, hp: 200);   // same fp, also in radius
        r.Aura(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)ExpectedHealed(200), m.U16s[first + Offsets.AHp]);
        Assert.Equal((ushort)200, m.U16s[twin + Offsets.AHp]);     // one heal per fingerprint
    }

    [Fact]
    public void Aura_reaches_the_diagonal_neighbor_Chebyshev()
    {
        // Pins Renewal's Chebyshev diagonal reach through the shared core's INJECTED metric:
        // at radius 1 the diagonal tile is distance 1 under Chebyshev (healed here) where a
        // Manhattan metric would score distance 2 and skip it. Deliberate, not vestigial --
        // this is the one pin proving Config.InRange really is Renewal's own metric.
        var m = PulseRig(out var r);
        long e = SeatAt(m, slot: 30, gx: 6, gy: 6, hp: 200);
        r.Aura(wgx: 5, wgy: 5, radius: 1, turn: 1);
        Assert.Equal((ushort)ExpectedHealed(200), m.U16s[e + Offsets.AHp]);
    }
}
