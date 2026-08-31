using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LivingWeapon;

/// <summary>One extended-inventory item as the boot arm needs it: the two binary records the
/// game reads plus the donor ids the accessor stubs answer with.</summary>
internal sealed class ExtendedItemDef
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    /// <summary>12-byte ITEM_COMMON_DATA (ExtendedRecords.EncodeCatalogRecord).</summary>
    public byte[] CatalogRecord { get; init; } = Array.Empty<byte>();
    /// <summary>8-byte ITEM_WEAPON_DATA (ExtendedRecords.EncodeWeaponRow).</summary>
    public byte[] WeaponRow { get; init; } = Array.Empty<byte>();
    /// <summary>Vanilla id every per-category accessor answers as (type probe, validity, range
    /// index/base, the sibling accessors, the category getter).</summary>
    public int CloneDonor { get; init; }
    /// <summary>Vanilla id whose sprite/palette pair draws the swing.</summary>
    public int ArtDonor { get; init; }
    /// <summary>Bag copies granted the first time the item is seen with no sidecar entry (LW-348).</summary>
    public int SeedCount { get; init; }
    /// <summary>ITEM_SHOPS_DATA.ShopFlags: the towns whose shop stocks it (LW-354); 0 = nowhere.</summary>
    public ushort ShopFlags { get; init; }
}

/// <summary>
/// LW-346: reads <c>&lt;modDir&gt;/extended_inventory/{ItemData,ItemWeaponData,ItemExtendedData}.xml</c>
/// (written by tools/generate.py from the `extended` rows of data/items.json, in the modloader's
/// own row vocabulary) into <see cref="ExtendedItemDef"/>s. Validation is LOUD and total: any
/// problem in any row makes <see cref="Load"/> return zero items plus the reasons, and the boot
/// arm then does nothing (one log line), because a half-loaded catalog would hand the game a
/// zero record for an id it was told exists. Ids must be contiguous from
/// <see cref="ExtendedCatalog.FirstExtendedId"/> (the stubs index donors by id - 261).
/// A missing folder is not an error: it is the "no extended inventory shipped" state.
/// </summary>
internal static class ExtendedInventoryData
{
    public const string FolderName = "extended_inventory";

    public sealed class LoadResult
    {
        public IReadOnlyList<ExtendedItemDef> Items { get; init; } = Array.Empty<ExtendedItemDef>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
        public bool FolderPresent { get; init; }
        public bool Ok => Errors.Count == 0;
    }

    public static LoadResult Load(string modDir)
    {
        string dir = Path.Combine(modDir, FolderName);
        if (!Directory.Exists(dir)) return new LoadResult();
        var errors = new List<string>();
        var items = new List<ExtendedItemDef>();
        try
        {
            var catalog = Rows(Path.Combine(dir, "ItemData.xml"), "Item", errors);
            var weapons = Rows(Path.Combine(dir, "ItemWeaponData.xml"), "ItemWeapon", errors);
            var extended = Rows(Path.Combine(dir, "ItemExtendedData.xml"), "ItemExtended", errors);
            if (errors.Count > 0) return new LoadResult { Errors = errors, FolderPresent = true };

            var ids = extended.Keys.OrderBy(i => i).ToList();
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] != ExtendedCatalog.FirstExtendedId + i)
                {
                    errors.Add($"ids must be contiguous from {ExtendedCatalog.FirstExtendedId}: got [{string.Join(",", ids)}]");
                    break;
                }
            if (ids.Count > 0 && ids[^1] > ExtendedCatalog.LastExtendedId)
                errors.Add($"id {ids[^1]} is past the last extended slot {ExtendedCatalog.LastExtendedId}");
            // LW-368 round 2 (P11): past this many, seven of the boot-patch sites plus one
            // post-load site are `lea` disp8 bytes that overflow instead of widening, corrupting
            // a bound rather than raising it.
            if (ids.Count > ExtendedSites.MaxExtendedCount)
                errors.Add($"{ids.Count} extended items is past ExtendedSites.MaxExtendedCount ({ExtendedSites.MaxExtendedCount}): the eight disp8 lea sites cannot widen any further");
            foreach (int id in ids)
            {
                if (!catalog.TryGetValue(id, out var c)) { errors.Add($"id {id}: no ItemData.xml row"); continue; }
                if (!weapons.TryGetValue(id, out var w)) { errors.Add($"id {id}: no ItemWeaponData.xml row (V1 is weapons only)"); continue; }
                try { items.Add(Build(id, c, w, extended[id])); }
                catch (Exception ex) { errors.Add($"id {id}: {ex.Message}"); }
            }
            foreach (int id in catalog.Keys.Concat(weapons.Keys))
                if (!extended.ContainsKey(id)) errors.Add($"id {id}: row without an ItemExtendedData.xml entry");
        }
        catch (Exception ex) { errors.Add("unreadable: " + ex.Message); }
        return new LoadResult { Items = errors.Count == 0 ? items : Array.Empty<ExtendedItemDef>(), Errors = errors.Distinct().ToList(), FolderPresent = true };
    }

    private static ExtendedItemDef Build(int id, XElement c, XElement w, XElement e)
    {
        int clone = Int(e, "CloneDonorId"), art = Int(e, "ArtDonorId");
        if (clone < 1 || clone > 255) throw new FormatException($"CloneDonorId {clone} must be a vanilla-range id (1..255)");
        if (art < 1 || art > 255) throw new FormatException($"ArtDonorId {art} must be a vanilla-range id (1..255)");
        int price = Int(c, "Price");
        if (price < 0 || price > ushort.MaxValue) throw new FormatException($"Price {price} out of range");
        string category = Text(c, "ItemCategory");
        var record = ExtendedRecords.EncodeCatalogRecord(
            Byte(c, "Palette"), Byte(c, "SpriteID"), Byte(c, "RequiredLevel"),
            ExtendedRecords.ParseFlags(Text(c, "TypeFlags"), ExtendedRecords.TypeFlags, "TypeFlags"),
            Byte(c, "AdditionalDataId"), ExtendedRecords.ParseCategory(category), Byte(c, "EquipBonusId"),
            (ushort)price, ExtendedRecords.ParseNamed(Text(c, "ShopAvailability"), ExtendedRecords.ShopAvailability, "ShopAvailability"));
        var row = ExtendedRecords.EncodeWeaponRow(
            Byte(w, "Range"), ExtendedRecords.ParseFlags(Text(w, "AttackFlags"), ExtendedRecords.AttackFlags, "AttackFlags"),
            Byte(w, "Formula"), Byte(w, "Power"), Byte(w, "Evasion"),
            ExtendedRecords.ParseFlags(Text(w, "Elements"), ExtendedRecords.Elements, "Elements"), Byte(w, "OptionsAbilityId"));
        return new ExtendedItemDef
        {
            Id = id, Name = Text(e, "Name"), Category = category, CatalogRecord = record, WeaponRow = row,
            CloneDonor = clone, ArtDonor = art, SeedCount = Int(e, "SeedCount"),
            // <Shops> is optional (an older table without it = sold nowhere), the loader's own names.
            ShopFlags = ExtendedRecords.ParseShops(e.Element("Shops")?.Value),
        };
    }

    /// <summary>Id -&gt; row element for one file; a missing file or a duplicate id is an error.</summary>
    private static Dictionary<int, XElement> Rows(string path, string rowName, List<string> errors)
    {
        var map = new Dictionary<int, XElement>();
        if (!File.Exists(path)) { errors.Add($"{Path.GetFileName(path)}: missing"); return map; }
        try
        {
            var doc = XDocument.Load(path);
            var entries = doc.Root?.Element("Entries");
            if (entries == null) { errors.Add($"{Path.GetFileName(path)}: no <Entries>"); return map; }
            foreach (var row in entries.Elements(rowName))
            {
                int id = Int(row, "Id");
                if (!map.TryAdd(id, row)) errors.Add($"{Path.GetFileName(path)}: duplicate id {id}");
            }
        }
        catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        return map;
    }

    private static string Text(XElement row, string name)
        => row.Element(name)?.Value?.Trim() ?? throw new FormatException($"<{name}> missing");

    private static int Int(XElement row, string name)
        => int.TryParse(Text(row, name), out int v) ? v : throw new FormatException($"<{name}> is not a number");

    private static byte Byte(XElement row, string name)
    {
        int v = Int(row, name);
        return v >= 0 && v <= 255 ? (byte)v : throw new FormatException($"<{name}> {v} is not a byte");
    }
}
