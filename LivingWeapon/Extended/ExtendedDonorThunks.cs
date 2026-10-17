namespace LivingWeapon;

/// <summary>One accessor function entry the extended inventory hooks: its live address, a log
/// label, the bytes it must carry before we overwrite it (the trampoline re-executes them), and
/// the offset of the <c>E8 rel32</c> call inside them (-1 when there is none).</summary>
internal readonly record struct AccessorEntry(long Addr, string Label, byte[] Expected, int CallAt);

/// <summary>
/// Split out of ExtendedSites.cs when the byte-patch table passed ~200 lines.
/// LW-388 R12: on the Denuvo build each of these ten addresses held a five-byte <c>E9 rel32</c> thunk
/// into the protected tail. On the Denuvo-free build they are plain function entries, so the hook
/// steals real instructions (see <see cref="AccessorEntryHook"/>). Expected bytes were read from the
/// Denuvo-free exe on disk 2026-10-16 (Steam buildid 24304444). The two call entries both call the
/// range-index entry (0x1402B8BCC), which is itself hooked.
/// </summary>
internal static class ExtendedDonorThunks
{
    private static readonly byte[] SubRspMovzxR9 = { 0x48, 0x83, 0xEC, 0x28, 0x44, 0x0F, 0xB7, 0xC9 };   // sub rsp,28h; movzx r9d,cx

    /// <summary>The weapon-stat accessor, which gets the row stub. A property because <c>Addr</c> is
    /// rebased and a field would bake the image base at type-init.</summary>
    public static AccessorEntry WeaponStat =>
        new(Offsets.ThunkWeaponStat, "weapon-stat", SubRspMovzxR9, -1);

    /// <summary>The accessor entries every per-category lookup goes through, with the donor table
    /// each answers from (the art donor draws the swing, the clone donor answers the rest).</summary>
    public static (AccessorEntry Entry, bool UsesArtDonor)[] DonorThunks => new[]
    {
        (new AccessorEntry(Offsets.ThunkValidity, "validity",
            new byte[] { 0x0F, 0xB7, 0xC1, 0x41, 0xB8, 0x01, 0x00, 0x00, 0x00 }, -1), false),                   // movzx eax,cx; mov r8d,1
        (new AccessorEntry(Offsets.ThunkTypeProbe, "type-probe",
            new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x44, 0x8B, 0xCA }, -1), false),                               // sub rsp,28h; mov r9d,edx
        (new AccessorEntry(Offsets.ThunkRangeIndex, "range-index",
            new byte[] { 0xB8, 0xFD, 0x00, 0x00, 0x00 }, -1), false),                                           // mov eax,0FDh
        (new AccessorEntry(Offsets.ThunkRangeBase, "range-base",
            new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0xB7, 0xFF, 0xFF, 0xFF }, 4), false),                    // sub rsp,28h; call range-index
        (new AccessorEntry(Offsets.ThunkSibling1, "sibling-1", SubRspMovzxR9, -1), false),
        (new AccessorEntry(Offsets.ThunkSibling2, "sibling-2", SubRspMovzxR9, -1), false),
        (new AccessorEntry(Offsets.ThunkSibling3, "sibling-3", SubRspMovzxR9, -1), false),
        (new AccessorEntry(Offsets.ThunkSibling4, "sibling-4", SubRspMovzxR9, -1), false),
        (new AccessorEntry(Offsets.ThunkSpritePair, "sprite-pair",
            new byte[] { 0x48, 0x83, 0xEC, 0x28, 0xE8, 0x63, 0xFD, 0xFF, 0xFF }, 4), true),                     // sub rsp,28h; call range-index
    };
}
