using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The spans the table doors pin. Every byte was read from the exe file by
/// tools/probes/static_table_file_image_probe.py (stamp 0x6A5E4A1B). They are the game's own
/// instructions that address each table, so they ask where the table lives and never what it holds.
/// Addresses are properties so the image base is read at use.
/// </summary>
internal static class TableSignatures
{
    internal static readonly byte[] TerrainVetoTestBytes = { 0xF6, 0x84, 0xCF, 0xB6, 0xDC, 0xD8, 0x00, 0x03 };
    internal static long TerrainVetoTestSite => ImageBase.At(0x1402711E8);

    internal static readonly byte[] TerrainInspectReadBytes =
        { 0x8A, 0x8C, 0xCA, 0xB3, 0xDC, 0xD8, 0x00, 0x8A, 0x94, 0xC2, 0xB2, 0xDC, 0xD8, 0x00 };
    internal static long TerrainInspectReadSite => ImageBase.At(0x14027B264);

    internal static readonly byte[] PaletteClearBytes =
        { 0x48, 0x8D, 0x0D, 0x1C, 0x11, 0xB6, 0x00, 0x41, 0xB8, 0x00, 0x04, 0x00, 0x00 };
    internal static long PaletteClearSite => ImageBase.At(0x1401D462D);

    internal static readonly byte[] PaletteBankBBytes = { 0x48, 0x8D, 0x05, 0x6A, 0x6E, 0xAC, 0x00 };
    internal static long PaletteBankBSite => ImageBase.At(0x14026EADF);

    internal static readonly byte[] PoachStoreBytesA = { 0x48, 0x8D, 0x35, 0x2A, 0x7A, 0xFF, 0x00 };
    internal static long PoachStoreSiteA => ImageBase.At(0x1401AFFEA);

    internal static readonly byte[] PoachStoreBytesB = { 0x4C, 0x8D, 0x3D, 0xA1, 0xCC, 0xFF, 0x00 };
    internal static long PoachStoreSiteB => ImageBase.At(0x1401AAD73);

    internal static readonly byte[] InflictTableBytes = { 0x48, 0x8D, 0x15, 0xAC, 0x52, 0x71, 0x00 };
    internal static long InflictTableSite => ImageBase.At(0x1400FA8ED);

    internal static readonly byte[] ItemStatsBytes = { 0x49, 0x8D, 0x88, 0x90, 0xF6, 0x80, 0x00 };
    internal static long ItemStatsSite => ImageBase.At(0x1402B8CBC);

    /// <summary>The first four bytes of every action table row, identical in the file template and the filled table.</summary>
    internal static readonly byte[] ActionRowMarker = { 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>Eight non gun items whose vanilla and shipped WP differ by four or more, none with a wp lane.</summary>
    internal static readonly int[] PinnedItemIds = { 106, 94, 121, 93, 47, 7, 92, 11 };

    internal static readonly byte[][] PinnedItemVanillaRows =
    {
        new byte[] { 0x02, 0x46, 0x01, 0xFF, 0x1E, 0x0A, 0x00, 0x00 },
        new byte[] { 0x03, 0x20, 0x01, 0xFF, 0x0F, 0x0A, 0x00, 0x18 },
        new byte[] { 0x02, 0x40, 0x01, 0xFF, 0x0F, 0x32, 0x00, 0x00 },
        new byte[] { 0x03, 0x20, 0x06, 0xFF, 0x0D, 0x0A, 0x00, 0x00 },
        new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x19, 0x0F, 0x00, 0x00 },
        new byte[] { 0x01, 0x8A, 0x01, 0xFF, 0x07, 0x05, 0x00, 0x00 },
        new byte[] { 0x03, 0x20, 0x01, 0xFF, 0x0A, 0x0A, 0x00, 0x17 },
        new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x08, 0x05, 0x00, 0x00 },
    };

    /// <summary>Every code site span with its name and live address, for the contract tests.</summary>
    internal static IReadOnlyList<(string Name, long Addr, byte[] Bytes)> Sites => new[]
    {
        ("terrain-veto-test-site", TerrainVetoTestSite, TerrainVetoTestBytes),
        ("terrain-inspect-read-site", TerrainInspectReadSite, TerrainInspectReadBytes),
        ("palette-clear-site", PaletteClearSite, PaletteClearBytes),
        ("palette-bank-b-site", PaletteBankBSite, PaletteBankBBytes),
        ("poach-store-site-a", PoachStoreSiteA, PoachStoreBytesA),
        ("poach-store-site-b", PoachStoreSiteB, PoachStoreBytesB),
        ("inflict-table-site", InflictTableSite, InflictTableBytes),
        ("item-stats-site", ItemStatsSite, ItemStatsBytes),
    };
}
