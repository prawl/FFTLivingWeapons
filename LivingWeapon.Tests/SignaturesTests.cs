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
public class SignaturesTests
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

    // --- Unbroken Chant (Hushward Rod): Swiftspell rides the proven support-grant path ---

    [Fact]
    public void Swiftspell_at_P3_resolves_to_byte3_bit_0x08()
    {
        // id 226 - base 198 = pos 28 -> byte 3, mask 0x80 >> 4 = 0x08.
        Assert.True(Signatures.ResolveSupport(Sig(226, 3), tier: 3, out int off, out byte mask));
        Assert.Equal(3, off);
        Assert.Equal(0x08, mask);
        Assert.False(Signatures.ResolveSupport(Sig(226, 3), tier: 2, out _, out _));   // silent below P3
    }

    [Fact]
    public void Swiftspell_is_not_marked_build_time_only()
    {
        // Charge time is computed when a cast QUEUES (not at battle build), so a held live bit
        // plausibly works -- the open live-test question. The grant read-back log must not warn.
        Assert.False(Signatures.IsBuildTimeOnly(226));
    }

    // --- SupportBit: the pure encoder shared by ResolveSupport and Choir ---

    [Fact]
    public void SupportBit_NonCharge_encodes_byte3_mask0x04()
    {
        // id 227 - base 198 = pos 29 -> byte 3, mask 0x80 >> 5 = 0x04.
        Assert.True(Signatures.SupportBit(227, out int off, out byte mask));
        Assert.Equal(3, off);
        Assert.Equal(0x04, mask);
    }

    [Fact]
    public void SupportBit_id_198_encodes_byte0_mask0x80()
    {
        Assert.True(Signatures.SupportBit(198, out int off, out byte mask));
        Assert.Equal(0, off);
        Assert.Equal(0x80, mask);
    }

    [Theory]
    [InlineData(197)]   // below the support field
    [InlineData(230)]   // movement base, outside the field
    public void SupportBit_out_of_field_returns_false(int id)
        => Assert.False(Signatures.SupportBit(id, out _, out _));

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

    // --- Spiritual Font (Wellspring Rod): movement-bit grants (Lifefont + Manafont) ---
    // The movement bitfield sits at +0x9C (3 bytes, base id 230, MSB-first -- the same shape
    // as the support field). Movement is normally exactly-one-effective; OR-setting BOTH font
    // bits is the open live question the every-hold read-back log answers.

    [Theory]
    [InlineData(230, 0, 0x80)]   // Move +1: first movement id -> byte 0, top bit
    [InlineData(237, 0, 0x01)]   // Lifefont
    [InlineData(238, 1, 0x80)]   // Manafont (crosses into byte 1)
    [InlineData(242, 1, 0x08)]   // Teleport
    [InlineData(243, 1, 0x04)]   // Master Teleportation (ability.en key 499)
    [InlineData(253, 2, 0x01)]   // last id in the 3-byte field
    public void ResolveMovement_encodes_msb_first(int id, int expectOff, int expectMask)
    {
        Assert.True(Signatures.ResolveMovement(id, out int off, out byte mask));
        Assert.Equal(expectOff, off);
        Assert.Equal(expectMask, mask);
    }

    [Theory]
    [InlineData(229)]   // support band, below the field
    [InlineData(254)]   // past the 3-byte field
    public void ResolveMovement_rejects_ids_outside_the_field(int id)
        => Assert.False(Signatures.ResolveMovement(id, out _, out _));

    // ResolveMovementGrant: the PURE grant decision (the tier gate lives HERE, tested).
    // Empty list == a hold writes nothing. The generic encoder for any future movement-bit
    // grant; no data ships one today (Spiritual Font's live test proved the engine honors
    // exactly ONE movement passive, so its font bits were retired for a runtime restore).

    [Fact]
    public void ResolveMovementGrant_is_empty_below_the_tier()
    {
        Assert.Empty(Signatures.ResolveMovementGrant(new[] { 237, 238 }, atTier: 3, tier: 2));
        Assert.Empty(Signatures.ResolveMovementGrant(new[] { 237, 238 }, atTier: 3, tier: 0));
    }

    [Fact]
    public void ResolveMovementGrant_yields_both_font_encodings_at_tier()
    {
        var grants = Signatures.ResolveMovementGrant(new[] { 237, 238 }, atTier: 3, tier: 3);
        Assert.Equal(2, grants.Count);
        Assert.Equal((237, 0, (byte)0x01), grants[0]);   // Lifefont
        Assert.Equal((238, 1, (byte)0x80), grants[1]);   // Manafont
    }

    [Fact]
    public void ResolveMovementGrant_empty_when_null_unconfigured_or_out_of_field()
    {
        Assert.Empty(Signatures.ResolveMovementGrant(null, atTier: 3, tier: 3));
        Assert.Empty(Signatures.ResolveMovementGrant(System.Array.Empty<int>(), atTier: 3, tier: 3));
        Assert.Empty(Signatures.ResolveMovementGrant(new[] { 999 }, atTier: 3, tier: 3));
    }

    // --- unequip release: a granted support bit is AND-cleared when the granting weapon
    //     leaves the wielder's hands mid-battle (Rend/Steal mutate the roster live), so the
    //     buff doesn't linger to battle end. The player's own picked support is never stripped.

    [Theory]
    [InlineData(true, 0, 226, false)]      // still wielded -> never clear
    [InlineData(true, 226, 226, false)]
    [InlineData(false, 226, 226, false)]   // the player's own picked support -> never strip
    [InlineData(false, 0, 226, true)]      // unequipped, the bit was ours -> clear
    [InlineData(false, 213, 226, true)]    // a different player pick doesn't shield the grant
    public void ShouldClearOnUnequip_protects_the_players_pick(bool wielded, int picked, int ability, bool expected)
        => Assert.Equal(expected, Signatures.ShouldClearOnUnequip(wielded, picked, ability));

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

    // B3: a corrupt kills.json can produce negative counts. The modulo of a negative in C#
    // is also negative (-1 % 10000 == -1), producing a result with length > 4 which breaks
    // the fixed-width invariant. Clamp to non-negative before the modulo.

    [Theory]
    [InlineData(-1,     "0   ")]   // -1 clamps to 0
    [InlineData(-12345, "0   ")]   // any negative -> "0   "
    public void KillsSlot_negative_clamps_to_zero(int count, string expected)
    {
        Assert.Equal(expected, Signatures.KillsSlot(count));
    }

    [Fact]
    public void KillsSlot_negative_is_always_4_chars()
    {
        foreach (int n in new[] { -1, -9, -99, -999, -9999, -10000, -12345 })
            Assert.Equal(4, Signatures.KillsSlot(n).Length);
    }

    // --- StuckEdge: the once-per-transition latch behind the Barrage/ShadowBlade per-tick nags
    //     (logging overhaul). A condition that can hold true for MANY consecutive ticks (no empty
    //     JobCommand slot, a record not readable yet) must fire its diagnostic once on the rising
    //     edge, stay silent while stuck, and re-arm once the condition clears -- even at Debug tier,
    //     an every-33ms line would bloat the file for no new information.

    [Fact]
    public void StuckEdge_fires_once_on_the_rising_edge()
    {
        bool latched = false;
        Assert.True(Signatures.StuckEdge(ref latched, true));    // first stuck tick -> fires
        Assert.True(latched);
    }

    [Fact]
    public void StuckEdge_stays_silent_while_still_stuck()
    {
        bool latched = false;
        Signatures.StuckEdge(ref latched, true);
        Assert.False(Signatures.StuckEdge(ref latched, true));   // still stuck -> silent
        Assert.False(Signatures.StuckEdge(ref latched, true));   // still stuck -> silent
    }

    [Fact]
    public void StuckEdge_rearms_after_the_condition_clears()
    {
        bool latched = false;
        Signatures.StuckEdge(ref latched, true);     // fires, latches
        Assert.False(Signatures.StuckEdge(ref latched, false));  // cleared -> silent, re-arms
        Assert.False(latched);
        Assert.True(Signatures.StuckEdge(ref latched, true));    // stuck again -> fires again
    }

    [Fact]
    public void StuckEdge_never_fires_while_the_condition_never_holds()
    {
        bool latched = false;
        Assert.False(Signatures.StuckEdge(ref latched, false));
        Assert.False(Signatures.StuckEdge(ref latched, false));
    }
}
