using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Umbral Rod's "Life Sap" signature. At +3, a kill credited to the rod restores the wielder
/// by LifeSapPct (25%) of their max HP, clamped at full. The trigger is the per-weapon
/// kill-tally diff (the ExtraTurn freshKill pattern), so attribution rides the proven
/// KillTracker credit. The heal NEVER revives: a 0-HP wielder is left alone.
///
/// Pure jobs in LifeSap.Policy.cs:
///   (1) IsActive: gates on lifeSapOnKill AND tier >= AtTier.
///   (2) FreshKill: tally-diff with the -1 unprimed sentinel (no fire on first sight).
///   (3) HealAmount: round(maxHp * pct), floor 1 for any positive maxHp.
///   (4) NewHp: clamp at maxHp; hp <= 0 is untouched (never revive).
///   (5) WriteHp: guarded little-endian u16 write to the band entry's HP.
/// </summary>
public class LifeSapTests
{
    // Pinned buffers are committed addresses in our own process, so the production adapter's
    // RPM/WPM reads work on them for real -- the guard path is exercised, not faked.
    private static readonly LiveMemory Live = new();

    private static WeaponSignature SapSig(int atTier = 3) =>
        new() { AtTier = atTier, LifeSapOnKill = true, DisplayLabel = "Life Sap" };

    // ---- (1) IsActive ----

    [Fact]
    public void IsActive_false_when_no_signature()
        => Assert.False(LifeSap.IsActive(null, tier: 3));

    [Fact]
    public void IsActive_false_when_not_a_lifesap_weapon()
        => Assert.False(LifeSap.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));

    [Fact]
    public void IsActive_false_below_tier()
        => Assert.False(LifeSap.IsActive(SapSig(), tier: 2));

    [Fact]
    public void IsActive_true_at_and_above_tier()
    {
        Assert.True(LifeSap.IsActive(SapSig(), tier: 3));
        Assert.True(LifeSap.IsActive(SapSig(), tier: 4));
    }

    // ---- (2) FreshKill: the tally-diff trigger ----

    [Theory]
    [InlineData(-1, 0, false)]   // unprimed: first sight never fires
    [InlineData(-1, 5, false)]   // unprimed even with seeded kills
    [InlineData(0, 1, true)]
    [InlineData(5, 6, true)]
    [InlineData(5, 5, false)]    // no change
    [InlineData(5, 4, false)]    // tally can only climb; a drop is not a kill
    public void FreshKill_fires_only_on_a_primed_increment(int last, int now, bool expected)
        => Assert.Equal(expected, Signatures.FreshKill(last, now));

    // ---- (3) HealAmount: 25% of max, floor 1 ----

    [Theory]
    [InlineData(100, 25)]
    [InlineData(200, 50)]
    [InlineData(10, 3)]    // round(2.5) away from zero = 3
    [InlineData(2, 1)]     // round(0.5) away from zero = 1
    [InlineData(1, 1)]     // floor 1 (round(0.25) = 0 would be a dead grant)
    [InlineData(0, 0)]     // no maxHp -> no heal
    public void HealAmount_is_a_quarter_of_max_floor_1(int maxHp, int expected)
        => Assert.Equal(expected, LifeSap.HealAmount(maxHp, 0.25));

    // ---- (4) NewHp: clamp, never revive ----

    [Theory]
    [InlineData(50, 100, 25, 75)]
    [InlineData(90, 100, 25, 100)]   // clamped at full
    [InlineData(100, 100, 25, 100)]  // already full
    [InlineData(0, 100, 25, 0)]      // dead: NEVER revive
    [InlineData(1, 100, 25, 26)]
    public void NewHp_clamps_and_never_revives(int hp, int maxHp, int heal, int expected)
        => Assert.Equal(expected, LifeSap.NewHp(hp, maxHp, heal));

    // ---- (5) WriteHp: guarded little-endian write (pinned buffer stands in for the band) ----

    [Fact]
    public void WriteHp_writes_the_u16_little_endian()
    {
        using var band = PinnedBuf.Of(256);
        LifeSap.WriteHp(Live, band.Addr, 0x1234);
        Assert.Equal(0x34, band.Bytes[Offsets.AHp]);
        Assert.Equal(0x12, band.Bytes[Offsets.AHp + 1]);
    }

    // ---- Main-hand-only activation contract (B2) ----
    // A Living Weapon earns kills in any hand, but commands its gift only from the main hand.
    // LifeSap resolves wielder via Wielder.TryResolveMainHand (RRHand-only match).

    [Fact]
    public void ActivatesOnMainHandOnly_is_documented_in_policy()
        => Assert.True(LifeSap.ActivatesOnMainHandOnly);
}
