using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Pure decisions for Sanguine Sword's Shadow Blade signature, which grants the wielder Gaffgarion's
/// HP-absorbing dark strike through the same JobCommand injection that ships Barrage, reusing its table
/// primitives. Two differences from Barrage live here. Eligibility is a whitelist of sword command sets
/// (Squire/Knight, via the primary job or a mounted secondary) plus Ramza's story sword forms, and
/// NeedsInject takes any ability id. Still to confirm in game: that the granted ability shows its real
/// name and that every resolvable job executes it. The stateful runtime (ShadowBlade.cs) is the next step.
/// </summary>
internal static class ShadowBladePolicy
{
    /// <summary>Shadow Blade -- Gaffgarion's HP-draining dark sword strike (docs/research/FOLDABLE_ABILITIES.md id 165).</summary>
    public const int ShadowBladeAbilityId = 165;

    /// <summary>True when the signature is configured (GrantCommandAbilityId set) and the tier earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.GrantCommandAbilityId > 0;

    /// <summary>The sword-equipping command sets Shadow Blade may inhabit, Squire (record 5) and Knight
    /// (record 7). Samurai was dropped because it cannot equip swords in IC.</summary>
    public static readonly int[] AllowedRecords = { 5, 7 };   // Squire, Knight

    /// <summary>Ramza's story-unique sword jobs and the Mettle JobCommand record each resolves to
    /// (JobData.JobCommandId, records 25-28 are Mettle). Their job-bytes use the low story-unit encoding at
    /// roster +0x02 (read 2026-06-15, Gallant Knight = 3), so Barrage.TryResolveJob cannot map them.
    /// Job 7's Squire -> rec 31 is not Mettle and is excluded, as are the other story knights.</summary>
    public static readonly Dictionary<int, int> RamzaSwordJobs = new()
    {
        { 1, 25 },   // Squire (story variant)  -> Mettle rec 25
        { 2, 26 },   // Squire (story variant)  -> Mettle rec 26
        { 3, 27 },   // Gallant Knight          -> Mettle rec 27  (live-confirmed: roster job-byte 3)
        { 4, 28 },   // Squire (story variant)  -> Mettle rec 28
    };

    /// <summary>Mettle's learned-bitfield index on the roster. Barrage showed Mettle executes an injected ability at 0.</summary>
    public const int MettleJobIdx = 0;

    /// <summary>True when the primary job or mounted secondary resolves to an allowed sword set or a Ramza sword form.</summary>
    public static bool IsEligible(int job, int secondaryRecord)
        => TryResolveGrant(job, secondaryRecord, out _, out _, out _);

    /// <summary>Resolve the JobCommand record to inject into. Order is Ramza's sword forms, then an allowed
    /// primary job, then an allowed mounted secondary (so a Dragoon with a Knight secondary still qualifies).</summary>
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

    /// <summary>Idempotent inject check for any ability id. The slot needs a write when its byte differs or
    /// its extend bit disagrees with whether the id is &gt;= 256.</summary>
    public static bool NeedsInject(byte slotByte, ushort extAb, int slotIdx, int abilityId)
    {
        bool extWanted = abilityId >= 256;
        bool extHas = (extAb & Barrage.ExtendBit(slotIdx)) != 0;
        return slotByte != Barrage.SlotByte(abilityId) || extHas != extWanted;
    }
}
