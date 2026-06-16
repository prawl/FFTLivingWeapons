using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Shadow Blade decisions: active-gating, WHITELISTED sword-set eligibility (Squire/Knight
/// records, reached via the primary job or a mounted secondary -- not Thief-only like Barrage, and not
/// open to every job), and a general inject check that works for any ability id. The injection table
/// math itself is the proven Barrage code these reuse, covered by BarrageTests.
/// </summary>
public class ShadowBladeTests
{
    private const int ShadowBlade = ShadowBladePolicy.ShadowBladeAbilityId;   // 165

    [Fact]
    public void IsActiveRequiresAGrantedAbilityAndTheEarnedTier()
    {
        var sig = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = ShadowBlade };
        Assert.False(ShadowBladePolicy.IsActive(sig, tier: 2));
        Assert.True(ShadowBladePolicy.IsActive(sig, tier: 3));
        Assert.False(ShadowBladePolicy.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));  // no ability
        Assert.False(ShadowBladePolicy.IsActive(null, tier: 3));
    }

    [Fact]
    public void EligibleWhenThePrimaryJobIsAWhitelistedSwordSet()
    {
        // Knight (76) and Squire (74) -- the sword-equipping skill-sets ShadowBlade may land in.
        Assert.True(ShadowBladePolicy.IsEligible(job: 76, secondaryRecord: 0));   // Knight
        Assert.True(ShadowBladePolicy.IsEligible(job: 74, secondaryRecord: 0));   // Squire
    }

    [Fact]
    public void OffWhitelistJobIsNotEligibleEvenWhenItCanReceiveAbilities()
    {
        // White Mage (79) is a normal executor (Barrage COULD grant to it) but it's not a sword set;
        // Thief (83) -- Barrage's own home -- is off-theme here too; Samurai (88) was dropped because
        // it can't equip swords in IC. None is a whitelisted set -> no grant.
        Assert.False(ShadowBladePolicy.IsEligible(job: 79, secondaryRecord: 0));               // White Mage
        Assert.False(ShadowBladePolicy.IsEligible(job: Barrage.ThiefJob, secondaryRecord: 0)); // Thief
        Assert.False(ShadowBladePolicy.IsEligible(job: 88, secondaryRecord: 0));               // Samurai (no swords)
    }

    [Fact]
    public void SpecialExecutorPrimaryFallsBackToAWhitelistedSecondary()
    {
        // A Dragoon (87) primary is a special executor that can't hold a foreign ability, but with
        // Knight's Battle Skill (record 7) mounted as the secondary command, ShadowBlade lands in the
        // Knight skill set. The secondary fallback is KEPT (unlike Thief-only Barrage).
        Assert.True(ShadowBladePolicy.IsEligible(job: 87, secondaryRecord: 7));
        Assert.True(ShadowBladePolicy.TryResolveGrant(87, 7, out int recId, out int jobIdx, out bool viaSecondary));
        Assert.Equal(7, recId);      // the Knight command record
        Assert.Equal(2, jobIdx);     // Knight's learned-block index (same whether reached as primary or secondary)
        Assert.True(viaSecondary);
    }

    [Fact]
    public void SecondaryFallbackHonoursTheWhitelist_NotAnyGrantableCommand()
    {
        // The fallback only accepts a WHITELISTED secondary. Steal (record 14) made an Archer
        // Barrage-eligible, but it isn't a sword set -> Shadow Blade refuses it. Samurai (record 19)
        // is refused too now that Samurai is off the whitelist.
        Assert.False(ShadowBladePolicy.IsEligible(job: 77, secondaryRecord: Barrage.ThiefRecord)); // Archer/Steal
        Assert.False(ShadowBladePolicy.IsEligible(job: 77, secondaryRecord: 0));                   // Archer, no secondary
        Assert.False(ShadowBladePolicy.IsEligible(job: 87, secondaryRecord: 19));                  // Dragoon/Samurai -- dropped
        Assert.False(ShadowBladePolicy.IsEligible(job: 79, secondaryRecord: 11));                  // White Mage / Black Magic
    }

    [Fact]
    public void AWhitelistedPrimaryWinsOverTheSecondary()
    {
        // A Knight (whitelisted primary) with a Squire secondary mounted -> injected into the Knight set.
        Assert.True(ShadowBladePolicy.TryResolveGrant(76, 5, out int recId, out int jobIdx, out bool viaSecondary));
        Assert.Equal(7, recId);      // Knight's command record
        Assert.Equal(2, jobIdx);     // Knight's learned-block index
        Assert.False(viaSecondary);
    }

    [Fact]
    public void RamzaStorySwordFormsResolveToTheirMettleRecord()
    {
        // Ramza's Gallant Knight + Squire variants carry the Mettle command and a LOW story-unit
        // job-byte (live-read: Gallant Knight = 3) outside the 74-92 band, so they resolve straight to
        // their JobCommand record (the game's JobData.JobCommandId) at learned-block index 0 -- not via
        // the generic TryResolveJob and not via a secondary.
        Assert.True(ShadowBladePolicy.TryResolveGrant(3, secondaryRec: 6, out int rec, out int jobIdx, out bool viaSec));
        Assert.Equal(27, rec);   // Gallant Knight -> Mettle 27
        Assert.Equal(ShadowBladePolicy.MettleJobIdx, jobIdx);
        Assert.False(viaSec);

        Assert.True(ShadowBladePolicy.TryResolveGrant(1, 0, out rec, out _, out _)); Assert.Equal(25, rec); // Squire ch.1
        Assert.True(ShadowBladePolicy.TryResolveGrant(2, 0, out rec, out _, out _)); Assert.Equal(26, rec); // Squire ch.2
        Assert.True(ShadowBladePolicy.TryResolveGrant(4, 0, out rec, out _, out _)); Assert.Equal(28, rec); // Squire ch.4
        Assert.True(ShadowBladePolicy.IsEligible(job: 3, secondaryRecord: 0));                              // Gallant Knight wields it
    }

    [Fact]
    public void NonRamzaStoryJobsAreNotEligible()
    {
        // Job 7 ("Squire" but command record 31 -- NOT Mettle) and other special classes (Holy Knight 5,
        // Sword Saint 13) are different characters, not Ramza -- excluded even though some equip swords.
        Assert.False(ShadowBladePolicy.IsEligible(job: 7, secondaryRecord: 0));
        Assert.False(ShadowBladePolicy.IsEligible(job: 5, secondaryRecord: 0));
        Assert.False(ShadowBladePolicy.IsEligible(job: 13, secondaryRecord: 0));
    }

    [Fact]
    public void NeedsInjectIsTrueForAnEmptyOrWrongSlot_FalseWhenAlreadyCorrect()
    {
        // ShadowBlade is < 256, so its slot byte is 165 and its extend bit must be CLEAR.
        Assert.True(ShadowBladePolicy.NeedsInject(slotByte: 0, extAb: 0, slotIdx: 0, abilityId: ShadowBlade));     // empty
        Assert.True(ShadowBladePolicy.NeedsInject(slotByte: 99, extAb: 0, slotIdx: 0, abilityId: ShadowBlade));    // wrong byte
        Assert.False(ShadowBladePolicy.NeedsInject(slotByte: 165, extAb: 0, slotIdx: 0, abilityId: ShadowBlade));  // already correct
    }

    [Fact]
    public void NeedsInjectIsTrueWhenTheExtendBitDisagrees()
    {
        // Right byte but a stray extend bit set (would mean ability 256+165) -> still needs a fix.
        ushort strayExt = Barrage.ExtendBit(0);
        Assert.True(ShadowBladePolicy.NeedsInject(slotByte: 165, extAb: strayExt, slotIdx: 0, abilityId: ShadowBlade));
    }
}
