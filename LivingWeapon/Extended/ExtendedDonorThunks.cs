namespace LivingWeapon;

/// <summary>
/// LW-365: split out of ExtendedSites.cs (the byte-patch table crossed ~200 lines once the last
/// nine hand-read bounds landed). This is a different seam from BootSites/PostLoadSites -- a
/// thunk-clone map, not a byte patch -- with its own consumer (ExtendedInventory.Arm.Install)
/// and its own test coverage.
/// </summary>
internal static class ExtendedDonorThunks
{
    /// <summary>The accessor thunks every per-category lookup goes through, with which donor
    /// table each one answers from: the art donor draws the swing, the clone donor answers for
    /// everything else (type, validity, range, the sibling per-item tables).</summary>
    public static readonly (long Addr, string Label, bool UsesArtDonor)[] DonorThunks =
    {
        (Offsets.ThunkValidity, "validity", false),
        (Offsets.ThunkTypeProbe, "type-probe", false),
        (Offsets.ThunkRangeIndex, "range-index", false),
        (Offsets.ThunkRangeBase, "range-base", false),
        (Offsets.ThunkSibling1, "sibling-1", false),
        (Offsets.ThunkSibling2, "sibling-2", false),
        (Offsets.ThunkSibling3, "sibling-3", false),
        (Offsets.ThunkSibling4, "sibling-4", false),
        (Offsets.ThunkSpritePair, "sprite-pair", true),
    };
}
