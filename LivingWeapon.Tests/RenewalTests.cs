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
    // which is the exact case that is FALSE under Wyrmblood's Manhattan metric.

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
}
