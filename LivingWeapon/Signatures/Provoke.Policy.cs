namespace LivingWeapon;

/// <summary>
/// Pure decisions behind the Defender's "Provoke" signature: repoint ability 189
/// (vanilla Embrace) to plant an inert mark while a tier-3 main-hand Defender wielder exists,
/// and inject the renamed command into the wielder's JobCommand list. Provoke.cs has the
/// armed-or-inert story (id 33's items.json signature block, back since 43de63e).
///
/// Grant resolution is shared with ShadowBladePolicy (same Squire/Knight whitelist), so a change
/// to one moves the other; split them if they ever need to diverge.
///
/// The ability action table (<see cref="Offsets.LiveActionTable"/>) is 368 rows of 20 bytes,
/// duplicated byte-for-byte one table-length earlier (<see cref="DecoyActionTable"/>). The decoy
/// takes writes and reads them back but the game ignores it, so only the live copy is written.
/// Row byte +15 is an index into the inflict-status table (<see cref="Offsets.InflictTable"/>,
/// 128 rows of 6 bytes, mode byte first; a test pins the order after an off-by-one cost a live
/// cycle). Row <see cref="ProvokeInflictRow"/> (29) was unused: mode 0x80 (AllOrNothing), then
/// s0 bit 0x80 (StatusEffectData id 0, the blank slot docs/PROVOKE_AC.md uses as the mark).
/// Observed 2026-07-22 (LIVE_LEDGER row, Uncertain).
/// </summary>
internal static class ProvokePolicy
{
    /// <summary>Ability 189, vanilla Embrace, renamed via tools/patch_ability_names.py. Tests only;
    /// production reads meta.Signature.GrantCommandAbilityId.</summary>
    public const int ProvokeAbilityId = 189;

    /// <summary>True when a granted ability id is set and the kill tier is earned.</summary>
    public static bool IsActive(WeaponSignature? sig, int tier)
        => Signatures.Earned(sig, tier) && sig!.GrantCommandAbilityId > 0;

    /// <summary>Resolve a wielder to the JobCommand record and learned index to inject into.</summary>
    public static bool TryResolveGrant(int job, int secondaryRec, out int recId, out int jobIdx, out bool viaSecondary)
        => ShadowBladePolicy.TryResolveGrant(job, secondaryRec, out recId, out jobIdx, out viaSecondary);

    /// <summary>The decoy mirror of <see cref="Offsets.LiveActionTable"/>. Never written. It lives here
    /// because only a test can catch a mis-pin (both copies hold identical data).</summary>
    public static long DecoyActionTable => ImageBase.At(0x14078961C);

    public const int ActionStride = 20;
    public const int ActionRows = 368;
    public const int InflictStride = 6;
    public const int InflictRows = 128;

    /// <summary>Byte offset of InflictStatus within an action row.</summary>
    public const int ActionInflictOffset = 15;

    /// <summary>The unused inflict-table row Provoke authors; also the InflictStatus index value.</summary>
    public const int ProvokeInflictRow = 29;

    /// <summary>The six bytes row 29 must hold: mode 0x80, then s0 bit 0x80, s1..s4 clear.</summary>
    public static readonly byte[] DesiredInflictRow = { 0x80, 0x80, 0x00, 0x00, 0x00, 0x00 };

    /// <summary>Address of the InflictStatus byte for an ability id, in the live table only.</summary>
    public static long ActionInflictAddr(int abilityId) => Offsets.LiveActionTable + (long)abilityId * ActionStride + ActionInflictOffset;

    /// <summary>Address of the first byte of the given inflict-table row.</summary>
    public static long InflictRowAddr(int row) => Offsets.InflictTable + (long)row * InflictStride;

    /// <summary>True when the id is inside the live action table (0..ActionRows-1). A typo'd id
    /// (1890 for 189) would otherwise write far past the table; Provoke.WriteTable refuses and logs.
    /// Same fail-closed idea as tools/generate.py's OptionsAbilityId check.</summary>
    public static bool IsValidAbilityId(int abilityId) => abilityId >= 0 && abilityId < ActionRows;

    /// <summary>True when the row is inside the inflict-status table (0..InflictRows-1). Currently
    /// always true for the constant row; kept to pair with <see cref="IsValidAbilityId"/>.</summary>
    public static bool IsValidInflictRow(int row) => row >= 0 && row < InflictRows;

    /// <summary>True when the InflictStatus byte does not yet point at <see cref="ProvokeInflictRow"/>.</summary>
    public static bool NeedsActionWrite(byte currentByte) => currentByte != (byte)ProvokeInflictRow;

    /// <summary>True when byte idx (0..5) of the inflict row differs from <see cref="DesiredInflictRow"/>.</summary>
    public static bool NeedsInflictByteWrite(byte currentByte, int idx) => currentByte != DesiredInflictRow[idx];
}
