using System;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-346 S3: the extended-inventory XML loader, over fixtures written into a temp mod
/// dir, plus one pin against the SHIPPED mod/extended_inventory folder so tools/generate.py's
/// output and this loader can never disagree about the Moonblade.</summary>
public class ExtendedInventoryDataTests : IDisposable
{
    private readonly string _modDir = Path.Combine(Path.GetTempPath(), "lw_ext_" + Guid.NewGuid().ToString("N"));

    public ExtendedInventoryDataTests() => Directory.CreateDirectory(Path.Combine(_modDir, ExtendedInventoryData.FolderName));
    public void Dispose() { try { Directory.Delete(_modDir, true); } catch { } }

    private const string ItemRow261 =
        "<Item><Id>261</Id><Palette>4</Palette><SpriteID>22</SpriteID><RequiredLevel>0</RequiredLevel><TypeFlags>Weapon</TypeFlags>" +
        "<AdditionalDataId>37</AdditionalDataId><ItemCategory>Sword</ItemCategory><EquipBonusId>0</EquipBonusId><Price>10</Price><ShopAvailability>Blank</ShopAvailability></Item>";
    private const string WeaponRow261 =
        "<ItemWeapon><Id>261</Id><Range>1</Range><AttackFlags>Throwable, TwoSwords, TwoHands, Striking</AttackFlags><Formula>1</Formula>" +
        "<Power>15</Power><Evasion>0</Evasion><Elements>None</Elements><OptionsAbilityId>0</OptionsAbilityId></ItemWeapon>";
    private const string ExtRow261 =
        "<ItemExtended><Id>261</Id><Name>Moonblade</Name><CloneDonorId>37</CloneDonorId><ArtDonorId>37</ArtDonorId><SeedCount>1</SeedCount><Shops>Dorter, Gariland</Shops></ItemExtended>";

    private void Write(string items = ItemRow261, string weapons = WeaponRow261, string ext = ExtRow261)
    {
        string dir = Path.Combine(_modDir, ExtendedInventoryData.FolderName);
        File.WriteAllText(Path.Combine(dir, "ItemData.xml"), $"<ItemTable><Version>1</Version><Entries>{items}</Entries></ItemTable>");
        File.WriteAllText(Path.Combine(dir, "ItemWeaponData.xml"), $"<ItemWeaponTable><Version>1</Version><Entries>{weapons}</Entries></ItemWeaponTable>");
        File.WriteAllText(Path.Combine(dir, "ItemExtendedData.xml"), $"<ItemExtendedTable><Version>1</Version><Entries>{ext}</Entries></ItemExtendedTable>");
    }

    [Fact]
    public void Loads_the_moonblade_into_both_records_and_its_donors()
    {
        Write();
        var r = ExtendedInventoryData.Load(_modDir);
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        Assert.True(r.FolderPresent);
        var m = Assert.Single(r.Items);
        Assert.Equal(261, m.Id);
        Assert.Equal("Moonblade", m.Name);
        Assert.Equal("Sword", m.Category);
        Assert.Equal(new byte[] { 0x04, 0x16, 0x00, 0x80, 0x25, 0x03, 0x00, 0x00, 0x0A, 0x00, 0x00, 0x00 }, m.CatalogRecord);
        Assert.Equal(new byte[] { 0x01, 0x8E, 0x01, 0xFF, 0x0F, 0x00, 0x00, 0x00 }, m.WeaponRow);
        Assert.Equal(37, m.CloneDonor);
        Assert.Equal(37, m.ArtDonor);
        Assert.Equal(1, m.SeedCount);
        Assert.Equal(0x4002, m.ShopFlags);   // LW-354: Dorter | Gariland
    }

    [Fact]
    public void A_missing_folder_is_the_quiet_nothing_shipped_state_not_an_error()
    {
        var r = ExtendedInventoryData.Load(Path.Combine(_modDir, "nowhere"));
        Assert.True(r.Ok);
        Assert.False(r.FolderPresent);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Empty_tables_load_as_zero_items_without_errors()
    {
        Write("", "", "");
        var r = ExtendedInventoryData.Load(_modDir);
        Assert.True(r.Ok);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void Any_bad_row_yields_zero_items_and_names_the_problem()
    {
        Write(items: ItemRow261.Replace("<ItemCategory>Sword</ItemCategory>", "<ItemCategory>Greatsword</ItemCategory>"));
        var r = ExtendedInventoryData.Load(_modDir);
        Assert.False(r.Ok);
        Assert.Empty(r.Items);
        Assert.Contains(r.Errors, e => e.Contains("261") && e.Contains("Greatsword"));

        Write(weapons: WeaponRow261.Replace("<Power>15</Power>", "<Power>300</Power>"));
        Assert.Contains(ExtendedInventoryData.Load(_modDir).Errors, e => e.Contains("Power") && e.Contains("not a byte"));

        Write(weapons: WeaponRow261.Replace("<AttackFlags>Throwable, TwoSwords, TwoHands, Striking</AttackFlags>", "<AttackFlags>Sharp</AttackFlags>"));
        Assert.Contains(ExtendedInventoryData.Load(_modDir).Errors, e => e.Contains("AttackFlags") && e.Contains("Sharp"));

        Write(ext: ExtRow261.Replace("<CloneDonorId>37</CloneDonorId>", "<CloneDonorId>300</CloneDonorId>"));
        Assert.Contains(ExtendedInventoryData.Load(_modDir).Errors, e => e.Contains("CloneDonorId 300"));

        Write(ext: ExtRow261.Replace("<Shops>Dorter, Gariland</Shops>", "<Shops>Ivalice</Shops>"));
        Assert.Contains(ExtendedInventoryData.Load(_modDir).Errors, e => e.Contains("Ivalice"));

        Write(ext: ExtRow261.Replace("<Shops>Dorter, Gariland</Shops>", ""));   // an older table without <Shops> = sold nowhere, not an error
        Assert.Equal(0, Assert.Single(ExtendedInventoryData.Load(_modDir).Items).ShopFlags);
    }

    [Fact]
    public void Ids_must_be_contiguous_from_261_and_every_file_must_carry_the_row()
    {
        Write(items: ItemRow261.Replace("261", "262"), weapons: WeaponRow261.Replace("261", "262"), ext: ExtRow261.Replace("261", "262"));
        var gap = ExtendedInventoryData.Load(_modDir);
        Assert.Contains(gap.Errors, e => e.Contains("contiguous from 261"));
        Assert.Empty(gap.Items);

        Write(weapons: "");
        var noWeapon = ExtendedInventoryData.Load(_modDir);
        Assert.Contains(noWeapon.Errors, e => e.Contains("no ItemWeaponData.xml row"));

        Write(ext: "");
        var orphan = ExtendedInventoryData.Load(_modDir);
        Assert.Contains(orphan.Errors, e => e.Contains("without an ItemExtendedData.xml entry"));

        Write(items: ItemRow261 + ItemRow261);
        Assert.Contains(ExtendedInventoryData.Load(_modDir).Errors, e => e.Contains("duplicate id 261"));
    }

    [Fact]
    public void A_missing_or_malformed_file_is_an_error_not_a_crash()
    {
        Write();
        File.Delete(Path.Combine(_modDir, ExtendedInventoryData.FolderName, "ItemWeaponData.xml"));
        var missing = ExtendedInventoryData.Load(_modDir);
        Assert.Contains(missing.Errors, e => e.Contains("ItemWeaponData.xml: missing"));
        Assert.Empty(missing.Items);

        File.WriteAllText(Path.Combine(_modDir, ExtendedInventoryData.FolderName, "ItemWeaponData.xml"), "<ItemWeaponTable><Entries><ItemWeapon>");
        var broken = ExtendedInventoryData.Load(_modDir);
        Assert.False(broken.Ok);
        Assert.Empty(broken.Items);
    }

    [Fact]
    public void The_shipped_folder_loads_clean_and_defines_the_moonblade()
    {
        string repoRoot = RepoRoot();
        var r = ExtendedInventoryData.Load(Path.Combine(repoRoot, "mod"));
        Assert.True(r.FolderPresent, "mod/extended_inventory is missing: run tools/generate.py");
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        var m = Assert.Single(r.Items, i => i.Id == 261);
        Assert.Equal("Moonblade", m.Name);
        Assert.Equal(37, m.CloneDonor);
        Assert.Equal(0x0F, m.WeaponRow[4]);   // Power 15, the rig's proven damage row
        Assert.Equal(3, m.CatalogRecord[5]);   // Sword
        Assert.Equal(0x4000, m.ShopFlags);   // Dorter, the live-test placeholder
        Assert.All(r.Items, i => Assert.InRange(i.Id, ExtendedCatalog.FirstExtendedId, ExtendedCatalog.LastExtendedId));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found above the test bin dir");
    }
}
