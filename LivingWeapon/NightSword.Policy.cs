namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Sanguine Sword's "Night Sword" signature: grant the wielder
/// Gaffgarion's Shadowblade -- a ranged dark strike that absorbs the foe's HP -- via the same
/// JobCommand injection that ships Barrage. It REUSES the proven Barrage table primitives
/// (Barrage.TryResolveGrant / FindEmptySlot / ExtendBit / SlotByte / InjectSlot / RestoreRecord /
/// learned-bit math); the injection mechanism is identical. Only two things differ from Barrage and
/// live here, so the shipped Barrage module is never touched:
///   1. Eligibility is OPEN, not Thief-only -- any normal-executor job can hold a sword skill
///      (Shadowblade isn't class-flavoured like a thief's volley), so we gate on TryResolveGrant alone.
///   2. NeedsInject is GENERAL (any ability id) where Barrage's is hardcoded to 358 -> byte 102.
///
/// LIVE-PENDING (only the game can confirm, the same class of check Barrage needed): does the granted
/// Shadowblade render with its real NAME in the menu (the "blank-name" risk -- Barrage solved it for
/// an ability that has a table name, and Shadowblade 165 has one, so it SHOULD), and does every
/// resolvable job actually EXECUTE it (the special-executor swallow that made Barrage Thief-only)?
/// The stateful inject/restore/hold runtime (NightSword.cs) is the documented next step; this logic
/// layer is fully unit-tested first.
/// </summary>
internal static class NightSwordPolicy
{
    /// <summary>Shadowblade -- Gaffgarion's HP-draining dark sword strike (FOLDABLE_ABILITIES id 165).</summary>
    public const int ShadowbladeAbilityId = 165;

    /// <summary>True when the signature is configured (GrantCommandAbilityId set) and the tier earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.GrantCommandAbilityId > 0;

    /// <summary>A wielder qualifies whenever their primary job OR mounted secondary resolves to a
    /// grantable JobCommand record -- no Thief gate (the proven Barrage resolver already excludes the
    /// special-executor jobs that swallow a foreign ability).</summary>
    public static bool IsEligible(int job, int secondaryRecord)
        => Barrage.TryResolveGrant(job, secondaryRecord, out _, out _, out _);

    /// <summary>General idempotent-inject check for ANY ability id (Barrage's is 358-specific): the
    /// slot needs a write when its byte doesn't already hold the ability's low byte, or when its
    /// extend bit disagrees with whether the id is &gt;= 256 (the &gt;255 ability-256 case).</summary>
    public static bool NeedsInject(byte slotByte, ushort extAb, int slotIdx, int abilityId)
    {
        bool extWanted = abilityId >= 256;
        bool extHas = (extAb & Barrage.ExtendBit(slotIdx)) != 0;
        return slotByte != Barrage.SlotByte(abilityId) || extHas != extWanted;
    }
}
