using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The Living-Weapon SIGNATURE resolver -- the pure decision behind a weapon granting
/// its iconic support passive at its max tier (the Gloomfang -> Concentration pilot).
/// Two jobs, both pure (no memory): (1) gate on kill-tier >= atTier, (2) encode the
/// support-ability id into its bit in the combat struct's 4-byte support bitfield
/// (+0x98, base id 198, MSB-first). GrowthEngine does the guarded write+hold from this.
/// </summary>
public class SignatureTests
{
    private static WeaponSignature Sig(int id, int atTier, string slot = "support") =>
        new() { AbilityId = id, Slot = slot, AtTier = atTier };

    [Fact]
    public void Concentration_at_P3_resolves_to_the_right_support_bit()
    {
        // id 213 - base 198 = pos 15 -> byte 1, mask 0x80 >> 7 = 0x01.
        Assert.True(Signatures.ResolveSupport(Sig(213, 3), tier: 3, out int off, out byte mask));
        Assert.Equal(1, off);
        Assert.Equal(0x01, mask);
    }

    [Fact]
    public void Not_granted_below_its_tier()
    {
        Assert.False(Signatures.ResolveSupport(Sig(213, 3), tier: 2, out _, out _));
        Assert.False(Signatures.ResolveSupport(Sig(213, 3), tier: 0, out _, out _));
    }

    [Fact]
    public void Granted_at_or_above_its_tier()
    {
        Assert.True(Signatures.ResolveSupport(Sig(213, 1), tier: 1, out _, out _));
        Assert.True(Signatures.ResolveSupport(Sig(213, 1), tier: 3, out _, out _));
    }

    [Theory]
    [InlineData(198, 0, 0x80)]   // first support id -> byte 0, top bit
    [InlineData(229, 3, 0x01)]   // last id in the 4-byte field -> byte 3, low bit
    public void Encodes_field_boundaries(int id, int expectOff, int expectMask)
    {
        Assert.True(Signatures.ResolveSupport(Sig(id, 0), tier: 0, out int off, out byte mask));
        Assert.Equal(expectOff, off);
        Assert.Equal(expectMask, mask);
    }

    [Fact]
    public void Rejects_ids_outside_the_support_field()
    {
        Assert.False(Signatures.ResolveSupport(Sig(230, 0), tier: 0, out _, out _));   // movement base, not support
        Assert.False(Signatures.ResolveSupport(Sig(197, 0), tier: 0, out _, out _));   // below the field
    }

    [Fact]
    public void Only_support_signatures_are_wired()
    {
        // Reaction/movement are deliberately NOT granted (they'd hijack the user's slot).
        Assert.False(Signatures.ResolveSupport(Sig(213, 3, "reaction"), tier: 3, out _, out _));
        Assert.False(Signatures.ResolveSupport(Sig(213, 3, "movement"), tier: 3, out _, out _));
    }

    [Fact]
    public void Null_signature_resolves_to_no_write()
    {
        Assert.False(Signatures.ResolveSupport(null, tier: 3, out _, out _));
    }

    // --- the shipping knife support signatures arm the correct bit ---

    [Theory]
    [InlineData(212, 1, 0x02)]   // Hushblade -> Magick Def Boost
    [InlineData(210, 1, 0x08)]   // Sanguine Gauche -> Defense Boost (was HP Boost; can't grant max-HP live)
    [InlineData(221, 2, 0x01)]   // Dual Wield (bit math kept; no weapon ships it -- Zwill's +3 is the extra turn)
    public void Knife_support_signatures_arm_their_bit_at_P3(int id, int expectOff, int expectMask)
    {
        Assert.True(Signatures.ResolveSupport(Sig(id, 3), tier: 3, out int off, out byte mask));
        Assert.Equal(expectOff, off);
        Assert.Equal(expectMask, mask);
        Assert.False(Signatures.ResolveSupport(Sig(id, 3), tier: 2, out _, out _));   // silent below P3
    }

    // --- conditional (HP-gated) signature, e.g. Mortal Coil: Attack Boost while HP < 50% ---

    [Fact]
    public void ConditionMet_true_when_no_condition_set()
    {
        // HpBelow defaults to 0 (always-on); hp/maxHp are irrelevant then.
        Assert.True(Signatures.ConditionMet(Sig(213, 3), hp: 999, maxHp: 1000));
        Assert.True(Signatures.ConditionMet(Sig(213, 3), hp: 1, maxHp: 1000));
    }

    [Theory]
    [InlineData(49, 100, true)]    // 49% < 50% -> armed
    [InlineData(50, 100, false)]   // exactly half is NOT below half
    [InlineData(99, 100, false)]
    [InlineData(1, 100, true)]
    public void ConditionMet_gates_on_hp_percent(int hp, int maxHp, bool expected)
    {
        var sig = new WeaponSignature { AbilityId = 209, Slot = "support", AtTier = 3, HpBelow = 50 };
        Assert.Equal(expected, Signatures.ConditionMet(sig, hp, maxHp));
    }

    [Fact]
    public void ConditionMet_safe_when_maxhp_nonpositive_or_null()
    {
        var sig = new WeaponSignature { AbilityId = 209, Slot = "support", AtTier = 3, HpBelow = 50 };
        Assert.False(Signatures.ConditionMet(sig, hp: 0, maxHp: 0));   // struct not located / dead
        Assert.False(Signatures.ConditionMet(null, hp: 1, maxHp: 100));
    }

    // --- KillsSlot: left-aligned digits in a fixed 4-char slot ---

    [Theory]
    [InlineData(0,     "0   ")]   // single digit -> digit then 3 spaces
    [InlineData(7,     "7   ")]
    [InlineData(42,    "42  ")]   // two digits -> 2 spaces
    [InlineData(137,   "137 ")]   // three digits -> 1 space
    [InlineData(1337,  "1337")]   // four digits -> exactly full
    [InlineData(12345, "2345")]   // wraps at 10000: 12345 % 10000 = 2345
    public void KillsSlot_formats_correctly(int count, string expected)
    {
        Assert.Equal(expected, Signatures.KillsSlot(count));
    }

    [Fact]
    public void KillsSlot_is_always_4_chars()
    {
        foreach (int n in new[] { 0, 1, 9, 10, 99, 100, 999, 1000, 9999, 10000 })
            Assert.Equal(4, Signatures.KillsSlot(n).Length);
    }
}
