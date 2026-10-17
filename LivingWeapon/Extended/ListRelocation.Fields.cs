using System;

namespace LivingWeapon;

/// <summary>
/// The field arithmetic of ListRelocation, kept in its own partial file.
/// Split out when the 55-site table pushed ListRelocation.cs past 200 lines. Same partial class as ListRelocation.cs
/// (lifecycle) and ListRelocation.Sites.cs (table).
/// </summary>
internal sealed partial class ListRelocation
{
    /// <summary>Reach check. The allocator window is 2 GB around the image base, but the first
    /// free region can land anywhere in it. Checks the full 64-bit distance of every field before anything is written,
    /// so the int cast in <see cref="NewField"/> cannot truncate. Null when all 55 fields reach.</summary>
    private static string? CheckReach(long page)
    {
        foreach (var site in Sites)
        {
            long distance = Distance(site, page);
            if (distance < int.MinValue || distance > int.MaxValue)
                return $"list-relocation: page 0x{page:X} is out of int32 reach of field 0x{Live(site):X}";
        }
        return null;
    }

    /// <summary>Pure. The int32 a field must hold to reach <paramref name="page"/>'s copy of its list, at <see cref="Site.Off"/> inside it.
    /// A <see cref="SiteKind.RipLea"/> field measures from its own address + 4 + <see cref="Site.Trail"/>; a
    /// <see cref="SiteKind.ImageRelative"/> field from the image base. Both are live, since the game indexes from the live base.
    /// Truncates unconditionally, so callers run <see cref="CheckReach"/> first (<see cref="ListRelocation.Install"/> does).</summary>
    internal static int NewField(Site site, long page) => (int)Distance(site, page);

    /// <summary>The un-truncated distance behind <see cref="NewField"/>, shared with <see cref="CheckReach"/>.</summary>
    private static long Distance(Site site, long page)
    {
        long listBase = page + (site.List == ListId.Count ? 0 : SiblingPageOffset);
        long newTarget = listBase + site.Off;
        return site.Kind == SiteKind.RipLea ? newTarget - (Live(site) + 4 + site.Trail) : newTarget - ImageBase.Base;
    }

    /// <summary><see cref="Site.Addr"/> is in pin space (the preferred-base address); this is where the field sits in the running image.</summary>
    private static long Live(Site site) => ImageBase.At(site.Addr);

    /// <summary>Pure. An int32 as its 4 little-endian bytes.</summary>
    internal static byte[] Encode(int value) => BitConverter.GetBytes(value);
}
