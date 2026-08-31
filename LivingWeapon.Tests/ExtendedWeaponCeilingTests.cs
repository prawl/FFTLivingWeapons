using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>LW-375 close-out (owner ruling 2026-08-31): the universal owned-at-once limit for the
/// extended catalog is the SHORTEST list in the item-display path, the equip picker's weapons-only
/// stack list, which <see cref="ListBuilderHook.StackCallerCap"/> caps at 149 entries (LW-371's
/// cookie arithmetic; LW-372's hook enforces it). 123 vanilla weapon kinds exist (121 items.json
/// weapon rows plus the two DLC blades 256/257), so at most 26 extended weapon kinds may ship
/// before a completionist who owns everything starts losing rows off the bottom of the equip
/// picker. The owner declined the third relocation that would have lifted it (a ~50-site
/// sweep-and-repoint of the static draw buffer 0x141811470) and ruled 123 + 26 the design
/// ceiling for every list instead; the All Items browser stays a 255-row window by design
/// (vanilla's own browser never drew past 145 of its 261).</summary>
public class ExtendedWeaponCeilingTests
{
    /// <summary>The ruling's arithmetic, pinned so the number moves VISIBLY if either input ever
    /// does (a new vanilla weapon row, a changed stack cap): 149 - 123 = 26.</summary>
    [Fact]
    public void The_picker_stack_list_leaves_room_for_exactly_26_extended_weapons()
    {
        Assert.Equal(123, CountVanillaWeaponKinds());
        Assert.Equal(26, ListBuilderHook.StackCallerCap - CountVanillaWeaponKinds());
    }

    /// <summary>THE GATE: the shipped extended folder may never define more weapon kinds than the
    /// picker has rows for. Red here means a completionist's equip picker would silently drop
    /// their last kinds; shrink the catalog or lift the picker ceiling first (the declined
    /// LW-375 relocation is the known lift).</summary>
    [Fact]
    public void The_shipped_extended_catalog_stays_inside_the_picker_ceiling()
    {
        var r = ExtendedInventoryData.Load(Path.Combine(RepoRoot(), "mod"));
        Assert.True(r.Ok, string.Join("; ", r.Errors));
        int extendedWeapons = r.Items.Count(i => WeaponCats.Contains(i.Category));
        int ceiling = ListBuilderHook.StackCallerCap - CountVanillaWeaponKinds();
        Assert.True(extendedWeapons <= ceiling,
            $"{extendedWeapons} extended weapon kinds ship but the equip picker only has room for {ceiling}: " +
            "a completionist who owns every weapon loses the rest off the picker's 149-entry stack list (LW-375 ruling).");
    }

    /// <summary>The 18 weapon categories (Throwing/Bomb are not picker kinds; Shield rides a
    /// different per-slot list), the TemplateRelocationTests.CountVanillaHandKinds set minus
    /// Shield.</summary>
    private static readonly HashSet<string> WeaponCats = new()
    {
        "Knife", "NinjaBlade", "Sword", "KnightSword", "Katana", "Axe", "Rod", "Staff",
        "Flail", "Gun", "Crossbow", "Bow", "Instrument", "Book", "Polearm", "Pole", "Bag", "Cloth",
    };

    /// <summary>123 = the vanilla items.json rows whose category is a weapon class (18 types,
    /// Throwing/Bomb excluded, Shield excluded: the picker's weapons-only list is mode 5) plus
    /// the two DLC blades 256/257 (not items.json rows) -- the weapons-only twin of
    /// TemplateRelocationTests.CountVanillaHandKinds.</summary>
    private static int CountVanillaWeaponKinds()
    {
        string path = Path.Combine(RepoRoot(), "data", "items.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int n = 0;
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            int id = item.GetProperty("id").GetInt32();
            string cat = item.GetProperty("category").GetString()!;
            if (id < 261 && WeaponCats.Contains(cat)) n++;
        }
        return n + 2;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found above the test bin dir");
    }
}
