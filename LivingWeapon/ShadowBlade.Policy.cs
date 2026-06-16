using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure decisions behind Sanguine Sword's "Shadow Blade" signature: grant the wielder
/// Gaffgarion's Shadow Blade -- a ranged dark strike that absorbs the foe's HP -- via the same
/// JobCommand injection that ships Barrage. It REUSES the proven Barrage table primitives
/// (Barrage.TryResolveJob / TryResolveSecondary / FindEmptySlot / ExtendBit / SlotByte / InjectSlot /
/// RestoreRecord / learned-bit math); the injection mechanism is identical. Only two things differ
/// from Barrage and live here, so the shipped Barrage module is never touched:
///   1. Eligibility is a WHITELIST of sword skill-sets -- Squire/Knight -- where Barrage is Thief-only.
///      Shadow Blade is a sword skill, so it may only land in one of those command sets, reached via the
///      wielder's PRIMARY job OR a MOUNTED SECONDARY command (e.g. a Dragoon with Knight's Battle Skill
///      mounted as its secondary gets it there). Plus Ramza's story-unique sword forms (his Gallant
///      Knight + Squire variants), which carry the Mettle command and a LOW story-unit job-byte the
///      generic resolver can't map -- see RamzaSwordJobs. See AllowedRecords / TryResolveGrant.
///   2. NeedsInject is GENERAL (any ability id) where Barrage's is hardcoded to 358 -> byte 102.
///
/// LIVE-PENDING (only the game can confirm, the same class of check Barrage needed): does the granted
/// Shadow Blade render with its real NAME in the menu (the "blank-name" risk -- Barrage solved it for
/// an ability that has a table name, and Shadow Blade 165 has one, so it SHOULD), and does every
/// resolvable job actually EXECUTE it (the special-executor swallow that made Barrage Thief-only)?
/// The stateful inject/restore/hold runtime (ShadowBlade.cs) is the documented next step; this logic
/// layer is fully unit-tested first.
/// </summary>
internal static class ShadowBladePolicy
{
    /// <summary>Shadow Blade -- Gaffgarion's HP-draining dark sword strike (FOLDABLE_ABILITIES id 165).</summary>
    public const int ShadowBladeAbilityId = 165;

    /// <summary>True when the signature is configured (GrantCommandAbilityId set) and the tier earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.GrantCommandAbilityId > 0;

    /// <summary>The command skill-sets Shadow Blade is allowed to inhabit -- the sword-equipping sets:
    /// Squire (record 5) and Knight (record 7). A wielder qualifies when one of these is their PRIMARY
    /// job command or a MOUNTED SECONDARY command. This is Shadow Blade's answer to Barrage's "Thief
    /// Only": the grant only ever lands in a sword set, never (say) a White Mage's spell list, even
    /// though the underlying injection could target any normal-executor record. (Samurai was dropped --
    /// Samurai can't equip swords in IC.)</summary>
    public static readonly int[] AllowedRecords = { 5, 7 };   // Squire, Knight

    /// <summary>Ramza's story-unique sword jobs and the JobCommand record each resolves to -- his
    /// Mettle command (the unique "Guts/Vaillance" skillset). Authoritative from the game's
    /// JobData.JobCommandId; the criterion for "a Ramza form" is a Mettle command record (25-28 are
    /// the four Mettle variants, and Mettle is Ramza-only). Their job-bytes live in the LOW story-unit
    /// encoding at roster +0x02 (live-read 2026-06-15: Gallant Knight = 3), NOT the 74-92 generic band,
    /// so they can't go through Barrage.TryResolveJob. Job 7's "Squire" -> rec 31 is NOT Mettle (a
    /// different special), so it's excluded; so are Holy Knight / Sword Saint / Fell Knight (other
    /// story characters). All four use the Mettle learned-block index 0 (the index Barrage proved live
    /// when Mettle executed an injected ability).</summary>
    public static readonly Dictionary<int, int> RamzaSwordJobs = new()
    {
        { 1, 25 },   // Squire (story variant)  -> Mettle rec 25
        { 2, 26 },   // Squire (story variant)  -> Mettle rec 26
        { 3, 27 },   // Gallant Knight          -> Mettle rec 27  (live-confirmed: roster job-byte 3)
        { 4, 28 },   // Squire (story variant)  -> Mettle rec 28
    };

    /// <summary>Mettle's learned-bitfield index (jobIdx) on the roster. Barrage proved Mettle executes
    /// an injected ability at jobIdx 0.</summary>
    public const int MettleJobIdx = 0;

    /// <summary>A wielder qualifies whenever their primary job OR mounted secondary resolves to a
    /// WHITELISTED sword skill-set (see <see cref="AllowedRecords"/>), or their job is one of Ramza's
    /// story-unique sword forms (see <see cref="RamzaSwordJobs"/>).</summary>
    public static bool IsEligible(int job, int secondaryRecord)
        => TryResolveGrant(job, secondaryRecord, out _, out _, out _);

    /// <summary>Resolve a wielder to the JobCommand record Shadow Blade should inject into, gated to
    /// the whitelisted sword sets. Order: (1) Ramza's story-unique sword forms resolve straight to
    /// their Mettle command record; (2) a generic PRIMARY job's command wins when it's a whitelisted
    /// set; (3) otherwise fall back to the MOUNTED SECONDARY command when THAT is whitelisted (so a
    /// special-executor primary like Dragoon still gets it via, e.g., a Knight secondary). False when
    /// none applies. Reuses the proven Barrage resolvers (TryResolveJob / TryResolveSecondary); only
    /// the whitelist filter and the Ramza map are added.</summary>
    public static bool TryResolveGrant(int job, int secondaryRec, out int recId, out int jobIdx, out bool viaSecondary)
    {
        viaSecondary = false;
        if (RamzaSwordJobs.TryGetValue(job, out recId))
        {
            jobIdx = MettleJobIdx;
            return true;
        }
        if (Barrage.TryResolveJob(job, out recId, out jobIdx)
            && System.Array.IndexOf(AllowedRecords, recId) >= 0)
            return true;
        if (System.Array.IndexOf(AllowedRecords, secondaryRec) >= 0
            && Barrage.TryResolveSecondary(secondaryRec, out jobIdx))
        {
            recId = secondaryRec;
            viaSecondary = true;
            return true;
        }
        recId = -1;
        jobIdx = -1;
        return false;
    }

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
