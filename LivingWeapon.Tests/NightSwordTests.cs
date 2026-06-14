using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Night Sword decisions: active-gating, OPEN job eligibility (any normal-executor job, not
/// Thief-only like Barrage), and a general inject check that works for any ability id. The injection
/// table math itself is the proven Barrage code these reuse, covered by BarrageTests.
/// </summary>
public class NightSwordTests
{
    private const int Shadowblade = NightSwordPolicy.ShadowbladeAbilityId;   // 165

    [Fact]
    public void IsActiveRequiresAGrantedAbilityAndTheEarnedTier()
    {
        var sig = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = Shadowblade };
        Assert.False(NightSwordPolicy.IsActive(sig, tier: 2));
        Assert.True(NightSwordPolicy.IsActive(sig, tier: 3));
        Assert.False(NightSwordPolicy.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));  // no ability
        Assert.False(NightSwordPolicy.IsActive(null, tier: 3));
    }

    [Fact]
    public void EligibilityIsOpenToAnyNormalExecutorJob_NotThiefOnly()
    {
        // Squire (74) is a normal-executor generic job -> eligible even with no secondary.
        Assert.True(NightSwordPolicy.IsEligible(job: 74, secondaryRecord: 0));
        // Thief (83) is eligible too (it always was) -- Night Sword just doesn't REQUIRE it.
        Assert.True(NightSwordPolicy.IsEligible(job: Barrage.ThiefJob, secondaryRecord: 0));
    }

    [Fact]
    public void SpecialExecutorJobWithNoSecondaryIsNotEligible()
    {
        // Archer (77) swallows foreign abilities via its special executor, and no secondary is
        // mounted -> not eligible (matches the proven Barrage resolver).
        Assert.False(NightSwordPolicy.IsEligible(job: 77, secondaryRecord: 0));
    }

    [Fact]
    public void SpecialExecutorJobBecomesEligibleViaAGrantableSecondary()
    {
        // Archer (77) primary but Steal (record 14) mounted as secondary -> eligible via the secondary.
        Assert.True(NightSwordPolicy.IsEligible(job: 77, secondaryRecord: Barrage.ThiefRecord));
    }

    [Fact]
    public void NeedsInjectIsTrueForAnEmptyOrWrongSlot_FalseWhenAlreadyCorrect()
    {
        // Shadowblade is < 256, so its slot byte is 165 and its extend bit must be CLEAR.
        Assert.True(NightSwordPolicy.NeedsInject(slotByte: 0, extAb: 0, slotIdx: 0, abilityId: Shadowblade));     // empty
        Assert.True(NightSwordPolicy.NeedsInject(slotByte: 99, extAb: 0, slotIdx: 0, abilityId: Shadowblade));    // wrong byte
        Assert.False(NightSwordPolicy.NeedsInject(slotByte: 165, extAb: 0, slotIdx: 0, abilityId: Shadowblade));  // already correct
    }

    [Fact]
    public void NeedsInjectIsTrueWhenTheExtendBitDisagrees()
    {
        // Right byte but a stray extend bit set (would mean ability 256+165) -> still needs a fix.
        ushort strayExt = Barrage.ExtendBit(0);
        Assert.True(NightSwordPolicy.NeedsInject(slotByte: 165, extAb: strayExt, slotIdx: 0, abilityId: Shadowblade));
    }
}
