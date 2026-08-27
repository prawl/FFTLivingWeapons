using System;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S2: the record encoders against the Chaos Blade's real bytes (1.5.2 exe on
/// disk, 2026-08-27) and the modloader enum tables.</summary>
public class ExtendedRecordsTests
{
    [Fact]
    public void Catalog_record_reproduces_the_chaos_blade_byte_for_byte()
    {
        var rec = ExtendedRecords.EncodeCatalogRecord(
            palette: 4, spriteId: 0x16, requiredLevel: 0x62,
            typeFlags: ExtendedRecords.ParseFlags("Rare, Weapon", ExtendedRecords.TypeFlags, "TypeFlags"),
            secondTableId: 37,
            category: ExtendedRecords.ParseCategory("KnightSword"),
            equipBonusId: 5, price: 10,
            shopAvailability: ExtendedRecords.ParseNamed("Unknown20", ExtendedRecords.ShopAvailability, "ShopAvailability"));
        Assert.Equal(new byte[] { 0x04, 0x16, 0x62, 0x82, 0x25, 0x04, 0x00, 0x05, 0x0A, 0x00, 0x14, 0x00 }, rec);
    }

    [Fact]
    public void Weapon_row_reproduces_the_chaos_blade_stats_row()
    {
        var row = ExtendedRecords.EncodeWeaponRow(
            range: 1,
            attackFlags: ExtendedRecords.ParseFlags("Striking, TwoSwords, TwoHands, Throwable", ExtendedRecords.AttackFlags, "AttackFlags"),
            formula: 1, power: 0x28, evasion: 0x14,
            elements: ExtendedRecords.ParseFlags("None", ExtendedRecords.Elements, "Elements"),
            optionsAbilityId: 0x11);
        Assert.Equal(new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x28, 0x14, 0x00, 0x11 }, row);
    }

    [Fact]
    public void Price_is_little_endian_and_flags_are_case_insensitive_or_ed()
    {
        var rec = ExtendedRecords.EncodeCatalogRecord(0, 0, 0, 0, 0, 0, 0, price: 0x1234, shopAvailability: 0);
        Assert.Equal(0x34, rec[8]);
        Assert.Equal(0x12, rec[9]);
        Assert.Equal(0x88, ExtendedRecords.ParseFlags("fire, earth", ExtendedRecords.Elements, "Elements"));
        Assert.Equal(0, ExtendedRecords.ParseFlags("", ExtendedRecords.Elements, "Elements"));
        Assert.Equal(0, ExtendedRecords.ParseFlags(null, ExtendedRecords.Elements, "Elements"));
        Assert.Equal(0x80, ExtendedRecords.ParseFlags("None, Weapon", ExtendedRecords.TypeFlags, "TypeFlags"));
    }

    [Fact]
    public void Unknown_tokens_fail_loudly_naming_the_field()
    {
        var ex = Assert.Throws<FormatException>(() => ExtendedRecords.ParseFlags("Striking, Sharp", ExtendedRecords.AttackFlags, "AttackFlags"));
        Assert.Contains("AttackFlags", ex.Message);
        Assert.Contains("Sharp", ex.Message);
        Assert.Throws<FormatException>(() => ExtendedRecords.ParseCategory("Greatsword"));
        Assert.Throws<FormatException>(() => ExtendedRecords.ParseCategory(""));
        Assert.Throws<FormatException>(() => ExtendedRecords.ParseNamed("Chapter9", ExtendedRecords.ShopAvailability, "ShopAvailability"));
    }

    [Theory]
    [InlineData("Knife", 1)]
    [InlineData("Sword", 3)]
    [InlineData("Axe", 6)]
    [InlineData("Flail", 9)]
    [InlineData("Pole", 16)]
    [InlineData("Cloth", 18)]
    [InlineData("Item", 34)]
    public void Category_codes_match_the_modloader_enum(string name, int code)
        => Assert.Equal(code, ExtendedRecords.ParseCategory(name));
}
