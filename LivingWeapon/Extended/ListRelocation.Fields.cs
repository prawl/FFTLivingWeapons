using System;

namespace LivingWeapon;

/// <summary>
/// The math that turns a field and our page into the new distance the field should hold.
/// </summary>
internal sealed partial class ListRelocation
{
    /// <summary>Null when every field reaches the page. A field far from the base can be out of reach even inside the
    /// allocator's 2 GB window, so each full 64 bit distance is checked before anything is written.</summary>
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

    /// <summary>The int32 that points a field at its list on <paramref name="page"/>, <see cref="Site.Off"/> bytes in.
    /// The cast drops high bits silently, which is why <see cref="ListRelocation.Install"/> runs <see cref="CheckReach"/> first.</summary>
    /// <remarks>A <see cref="SiteKind.RipLea"/> field counts from its own address + 4 + <see cref="Site.Trail"/>, and an
    /// <see cref="SiteKind.ImageRelative"/> one from wherever the image actually loaded.</remarks>
    internal static int NewField(Site site, long page) => (int)Distance(site, page);

    private static long Distance(Site site, long page)
    {
        long listBase = page + (site.List == ListId.Count ? 0 : SiblingPageOffset);
        long newTarget = listBase + site.Off;
        return site.Kind == SiteKind.RipLea ? newTarget - (Live(site) + 4 + site.Trail) : newTarget - ImageBase.Base;
    }

    /// <summary>Where the field sits in the running game.</summary>
    private static long Live(Site site) => ImageBase.At(site.Addr);

    internal static byte[] Encode(int value) => BitConverter.GetBytes(value);
}
