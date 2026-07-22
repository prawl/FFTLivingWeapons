using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The pure Provoke decisions: the tier/ability activation gate (shared shape with Barrage/
/// ShadowBlade), the delegated Squire/Knight grant resolution (reused from ShadowBladePolicy BY
/// DESIGN -- see ProvokePolicy's class doc), and the table-write predicates that decide whether
/// each of the seven guarded bytes still needs writing. The two live table BASES
/// (<see cref="Offsets.LiveActionTable"/>, <see cref="Offsets.InflictTable"/>) are pinned in
/// Offsets.cs alongside every other verified game address; this file pins the DECOY address and
/// the derived byte-level math that only a test (never a runtime check -- both copies hold
/// identical data) can catch a mis-pin on.
/// </summary>
public class ProvokePolicyTests
{
    [Fact]
    public void IsActiveRequiresAGrantedAbilityAndTheEarnedTier()
    {
        var sig = new WeaponSignature { AtTier = 3, GrantCommandAbilityId = ProvokePolicy.ProvokeAbilityId };
        Assert.False(ProvokePolicy.IsActive(sig, tier: 2));
        Assert.True(ProvokePolicy.IsActive(sig, tier: 3));
        Assert.False(ProvokePolicy.IsActive(new WeaponSignature { AtTier = 3 }, tier: 3));  // no ability
        Assert.False(ProvokePolicy.IsActive(null, tier: 3));
    }

    [Fact]
    public void TryResolveGrantDelegatesToTheSharedShadowBladeWhitelist()
    {
        // Knight (76), reached directly -- the same whitelist Shadow Blade uses, shared by design.
        Assert.True(ProvokePolicy.TryResolveGrant(job: 76, secondaryRec: 0, out int recId, out int jobIdx, out bool viaSecondary));
        Assert.Equal(7, recId);
        Assert.Equal(2, jobIdx);
        Assert.False(viaSecondary);

        // White Mage (79) is off the whitelist -- refused, same as Shadow Blade.
        Assert.False(ProvokePolicy.TryResolveGrant(job: 79, secondaryRec: 0, out _, out _, out _));
    }

    // --- Table pin: the negative half only a test can catch (see class doc) ---

    [Fact]
    public void Pinned_action_base_is_the_live_copy_and_not_the_decoy()
    {
        Assert.Equal(0x14078B2DCL, Offsets.LiveActionTable);
        Assert.Equal(0x14078961CL, ProvokePolicy.DecoyActionTable);
        Assert.Equal(ProvokePolicy.ActionRows * ProvokePolicy.ActionStride, Offsets.LiveActionTable - ProvokePolicy.DecoyActionTable);
        Assert.NotEqual(Offsets.LiveActionTable, ProvokePolicy.DecoyActionTable);
    }

    [Fact]
    public void Inflict_table_base_is_pinned()
    {
        Assert.Equal(0x14080FBA0L, Offsets.InflictTable);
    }

    [Fact]
    public void Ability_189_inflict_byte_address_is_derived_from_the_live_table_only()
    {
        // 189 * 20 + 15 = 3795; LiveActionTable + 3795 = 0x14078C1AF (spec-derived, LIVE_LEDGER row).
        Assert.Equal(0x14078C1AFL, ProvokePolicy.ActionInflictAddr(ProvokePolicy.ProvokeAbilityId));
    }

    [Fact]
    public void Authored_inflict_row_29_address_is_derived_from_the_inflict_table()
    {
        // InflictTable + 29 * 6 = 0x14080FC4E.
        Assert.Equal(0x14080FC4EL, ProvokePolicy.InflictRowAddr(ProvokePolicy.ProvokeInflictRow));
    }

    [Fact]
    public void Desired_inflict_row_is_mode_first_then_status_zero_bit()
    {
        Assert.Equal(new byte[] { 0x80, 0x80, 0x00, 0x00, 0x00, 0x00 }, ProvokePolicy.DesiredInflictRow);
    }

    [Fact]
    public void NeedsActionWriteIsTrueUntilTheByteEqualsTheAuthoredRowNumber()
    {
        Assert.True(ProvokePolicy.NeedsActionWrite(currentByte: 0));
        Assert.True(ProvokePolicy.NeedsActionWrite(currentByte: 65));   // some other pre-existing row (DontMove)
        Assert.False(ProvokePolicy.NeedsActionWrite(currentByte: (byte)ProvokePolicy.ProvokeInflictRow));
    }

    [Fact]
    public void NeedsInflictByteWriteComparesAgainstTheDesiredRowPerIndex()
    {
        Assert.True(ProvokePolicy.NeedsInflictByteWrite(currentByte: 0, idx: 0));    // wants 0x80
        Assert.False(ProvokePolicy.NeedsInflictByteWrite(currentByte: 0x80, idx: 0));
        Assert.False(ProvokePolicy.NeedsInflictByteWrite(currentByte: 0, idx: 2));   // wants 0
        Assert.True(ProvokePolicy.NeedsInflictByteWrite(currentByte: 1, idx: 2));
    }

    // --- Bounds guard: the pure half of Test C in ProvokeTests (not a substitute for it -- see
    // that file's class doc for why a module-level test is also required) ---

    [Fact]
    public void IsValidAbilityIdRejectsATypoPastActionRowsAndAcceptsTheRealOne()
    {
        Assert.False(ProvokePolicy.IsValidAbilityId(1890));   // 1890 >= ActionRows (368): a plausible 189 typo
        Assert.True(ProvokePolicy.IsValidAbilityId(189));
        Assert.False(ProvokePolicy.IsValidAbilityId(-1));
        Assert.True(ProvokePolicy.IsValidAbilityId(0));
        Assert.False(ProvokePolicy.IsValidAbilityId(ProvokePolicy.ActionRows));   // exclusive upper bound
    }

    [Fact]
    public void IsValidInflictRowAcceptsProvokesRowAndRejectsPastInflictRows()
    {
        Assert.True(ProvokePolicy.IsValidInflictRow(ProvokePolicy.ProvokeInflictRow));
        Assert.False(ProvokePolicy.IsValidInflictRow(-1));
        Assert.False(ProvokePolicy.IsValidInflictRow(ProvokePolicy.InflictRows));   // exclusive upper bound
    }
}
