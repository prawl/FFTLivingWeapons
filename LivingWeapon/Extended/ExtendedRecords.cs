using System;
using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-346: the pure encoders that turn an extended item's data-layer fields into the two binary
/// records the game reads: the 12-byte ITEM_COMMON_DATA catalog record and the 8-byte
/// ITEM_WEAPON_DATA stats row. Field order, sizes and every enum value are the modloader's own
/// (fftivc.utility.modloader.Interfaces/Tables/Structures/ITEM_COMMON_DATA.cs and
/// ITEM_WEAPON_DATA.cs, Nenkai, read 2026-08-27), which is also why the extended XML the data
/// layer emits uses those same names: anyone who can edit a vanilla item row can author one of
/// ours. Checked against the live bytes of the Chaos Blade (id 37) read from the 1.5.2 exe on
/// disk 2026-08-27: catalog <c>04 16 62 82 25 04 00 05 0A 00 14 00</c>, stats
/// <c>01 8E 01 FF 28 14 00 11</c> (byte +3 of a real stats row is 0xFF, mirrored here).
/// </summary>
internal static class ExtendedRecords
{
    public const int CatalogRecordSize = 12;
    public const int WeaponRowSize = 8;
    /// <summary>What every vanilla ITEM_WEAPON_DATA row carries in its unused byte +3.</summary>
    public const byte WeaponRowUnusedByte = 0xFF;

    public static readonly IReadOnlyDictionary<string, int> Categories = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["None"] = 0, ["Knife"] = 1, ["NinjaBlade"] = 2, ["Sword"] = 3, ["KnightSword"] = 4, ["Katana"] = 5,
        ["Axe"] = 6, ["Rod"] = 7, ["Staff"] = 8, ["Flail"] = 9, ["Gun"] = 10, ["Crossbow"] = 11, ["Bow"] = 12,
        ["Instrument"] = 13, ["Book"] = 14, ["Polearm"] = 15, ["Pole"] = 16, ["Bag"] = 17, ["Cloth"] = 18,
        ["Shield"] = 19, ["Helmet"] = 20, ["Hat"] = 21, ["HairAdornment"] = 22, ["Armor"] = 23, ["Clothing"] = 24,
        ["Robe"] = 25, ["Shoes"] = 26, ["Armguard"] = 27, ["Ring"] = 28, ["Armlet"] = 29, ["Cloak"] = 30,
        ["Perfume"] = 31, ["Throwing"] = 32, ["Bomb"] = 33, ["Item"] = 34,
    };

    public static readonly IReadOnlyDictionary<string, byte> TypeFlags = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["ImmuneToStealBreak"] = 1, ["Rare"] = 2, ["Unused_2"] = 4, ["Accessory"] = 8,
        ["Armor"] = 16, ["Headgear"] = 32, ["Shield"] = 64, ["Weapon"] = 128,
    };

    public static readonly IReadOnlyDictionary<string, byte> ShopAvailability = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["Blank"] = 0, ["Chapter1_Start"] = 1, ["Chapter1_EnterIgros"] = 2, ["Chapter1_SaveElmdor"] = 3,
        ["Chapter1_KillMiluda"] = 4, ["Chapter2_Start"] = 5, ["Chapter2_SaveOvelia"] = 6, ["Chapter2_MeetDraclau"] = 7,
        ["Chapter2_SaveAgrias"] = 8, ["Chapter3_Start"] = 9, ["Chapter3_Zalmo"] = 10, ["Chapter3_MeetVelius"] = 11,
        ["Chapter3_SaveRafa"] = 12, ["Chapter4_Start"] = 13, ["Chapter4_Bethla"] = 14, ["Chapter4_KillElmdor"] = 15,
        ["Chapter4_KillZalbag"] = 16, ["Unknown17"] = 17, ["Unknown18"] = 18, ["Unknown19"] = 19, ["Unknown20"] = 20,
    };

    public static readonly IReadOnlyDictionary<string, byte> AttackFlags = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["Striking"] = 128, ["Lunging"] = 64, ["Direct"] = 32, ["Arc"] = 16,
        ["TwoSwords"] = 8, ["TwoHands"] = 4, ["Throwable"] = 2, ["ForcedTwoHands"] = 1,
    };

    public static readonly IReadOnlyDictionary<string, byte> Elements = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
    {
        ["Fire"] = 128, ["Lightning"] = 64, ["Ice"] = 32, ["Wind"] = 16,
        ["Earth"] = 8, ["Water"] = 4, ["Holy"] = 2, ["Dark"] = 1,
    };

    /// <summary>ITEM_SHOPS_DATA.ShopFlags (u16, the modloader's ItemShopsData names): which towns
    /// stock the item. LW-354.</summary>
    public static readonly IReadOnlyDictionary<string, ushort> Shops = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["Gollund"] = 1 << 15, ["Dorter"] = 1 << 14, ["Zaland"] = 1 << 13, ["Goug"] = 1 << 12,
        ["Warjilis"] = 1 << 11, ["Bervenia"] = 1 << 10, ["SalGhidos"] = 1 << 9, ["Unused"] = 1 << 8,
        ["Lesalia"] = 1 << 7, ["Riovanes"] = 1 << 6, ["Eagrose"] = 1 << 5, ["Lionel"] = 1 << 4,
        ["Limberry"] = 1 << 3, ["Zeltennia"] = 1 << 2, ["Gariland"] = 1 << 1, ["Yardrow"] = 1 << 0,
    };

    /// <summary>Parses a ShopFlags list ("Dorter, Gariland"; "None" or empty = 0).</summary>
    public static ushort ParseShops(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return 0;
        ushort v = 0;
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Shops.TryGetValue(raw, out var bit)) throw new FormatException($"Shops: unknown town '{raw}'");
            v |= bit;
        }
        return v;
    }

    /// <summary>Parses a modloader flag list ("Throwable, TwoSwords, Striking"; "None" or empty
    /// = 0) against <paramref name="table"/>. Throws FormatException naming the bad token, so the
    /// loader's validation stays loud.</summary>
    public static byte ParseFlags(string? csv, IReadOnlyDictionary<string, byte> table, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(csv)) return 0;
        byte v = 0;
        foreach (var raw in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("None", StringComparison.OrdinalIgnoreCase)) continue;
            if (!table.TryGetValue(raw, out var bit))
                throw new FormatException($"{fieldName}: unknown flag '{raw}'");
            v |= bit;
        }
        return v;
    }

    public static byte ParseNamed(string? name, IReadOnlyDictionary<string, byte> table, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException($"{fieldName}: missing");
        if (!table.TryGetValue(name.Trim(), out var v)) throw new FormatException($"{fieldName}: unknown value '{name}'");
        return v;
    }

    public static byte ParseCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new FormatException("ItemCategory: missing");
        if (!Categories.TryGetValue(name.Trim(), out var v)) throw new FormatException($"ItemCategory: unknown value '{name}'");
        return (byte)v;
    }

    /// <summary>ITEM_COMMON_DATA, 12 bytes, Pack=1.</summary>
    public static byte[] EncodeCatalogRecord(byte palette, byte spriteId, byte requiredLevel, byte typeFlags,
        byte secondTableId, byte category, byte equipBonusId, ushort price, byte shopAvailability)
    {
        var r = new byte[CatalogRecordSize];
        r[0] = palette; r[1] = spriteId; r[2] = requiredLevel; r[3] = typeFlags; r[4] = secondTableId;
        r[5] = category; r[6] = 0; r[7] = equipBonusId;
        r[8] = (byte)(price & 0xFF); r[9] = (byte)(price >> 8);
        r[10] = shopAvailability; r[11] = 0;
        return r;
    }

    /// <summary>ITEM_WEAPON_DATA, 8 bytes, Pack=1 (byte +3 = 0xFF like every vanilla row).</summary>
    public static byte[] EncodeWeaponRow(byte range, byte attackFlags, byte formula, byte power, byte evasion,
        byte elements, byte optionsAbilityId)
        => new[] { range, attackFlags, formula, WeaponRowUnusedByte, power, evasion, elements, optionsAbilityId };
}
