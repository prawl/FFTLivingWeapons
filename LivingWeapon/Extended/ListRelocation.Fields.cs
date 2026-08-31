using System;

namespace LivingWeapon;

/// <summary>
/// LW-368 round 2b: the pure field-arithmetic half of ListRelocation, split out once the table's
/// growth (45 -> 55 sites, plus round 2b's <see cref="ListRelocation.Site.Trail"/>/<see
/// cref="ListRelocation.Site.Off"/>) pushed ListRelocation.cs past the 200-line refactor trigger.
/// Same partial class as ListRelocation.cs (the lifecycle: Install/Restore/the tripwire) and
/// ListRelocation.Sites.cs (the table); all three reach every name here by plain name. Kept
/// beside the table on purpose -- this is the arithmetic the table exists to feed.
/// </summary>
internal sealed partial class ListRelocation
{
    /// <summary>D6/REACH (v2.4, verifier finding b1): the allocator's window is 2 GB around the
    /// image base, but the FIRST free region inside that window can land anywhere in it -- on
    /// some machine, possibly more than 2 GB from a given site or from the image base itself.
    /// Checked here, before anything is written (not even the zero-fill), against the FULL
    /// 64-bit distance every field would otherwise need, so a truncating (int) cast in
    /// <see cref="NewField"/> can never silently point a field at garbage. Null when every one
    /// of the 55 fields still reaches.</summary>
    private static string? CheckReach(long page)
    {
        foreach (var site in Sites)
        {
            long distance = Distance(site, page);
            if (distance < int.MinValue || distance > int.MaxValue)
                return $"list-relocation: page 0x{page:X} is out of int32 reach of field 0x{site.Addr:X}";
        }
        return null;
    }

    /// <summary>Pure (D6): the int32 one field must hold to reach <paramref name="page"/>'s copy
    /// of its own list, at <see cref="Site.Off"/> inside it (0 for every field but round 2b's
    /// ten). A <see cref="SiteKind.RipLea"/> field measures from the next instruction -- its own
    /// address + 4, plus <see cref="Site.Trail"/> more for any immediate operand that trails the
    /// disp32; a <see cref="SiteKind.ImageRelative"/> field measures from the image base.
    /// Callers that write the result must go through <see cref="CheckReach"/> first
    /// (<see cref="ListRelocation.Install"/> does) -- this itself truncates unconditionally.</summary>
    internal static int NewField(Site site, long page) => (int)Distance(site, page);

    /// <summary>The un-truncated form of <see cref="NewField"/>'s distance, shared with
    /// <see cref="CheckReach"/> so the two can never compute it differently.</summary>
    private static long Distance(Site site, long page)
    {
        long listBase = page + (site.List == ListId.Count ? 0 : SiblingPageOffset);
        long newTarget = listBase + site.Off;
        return site.Kind == SiteKind.RipLea ? newTarget - (site.Addr + 4 + site.Trail) : newTarget - Offsets.ModuleBase;
    }

    /// <summary>Pure: an int32 as its 4 little-endian bytes.</summary>
    internal static byte[] Encode(int value) => BitConverter.GetBytes(value);
}
