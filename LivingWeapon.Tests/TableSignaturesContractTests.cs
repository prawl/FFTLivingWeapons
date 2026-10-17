using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LivingWeapon;
using Newtonsoft.Json.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-399: the pinned spans stay honest against the repo's own data, so a retune that closes a gap or gives a
/// pinned id a writer lane goes red here instead of silently weakening a door.
/// </summary>
public class TableSignaturesContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root not found above the test bin dir");
    }

    private static JToken ItemOf(int id)
    {
        var doc = JToken.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "items.json")));
        var items = doc is JArray a ? a : (JArray)doc["items"]!;
        return items.First(i => (int?)i["id"] == id);
    }

    [Fact]
    public void Eight_item_ids_are_pinned_with_one_vanilla_row_each()
    {
        Assert.Equal(new[] { 106, 94, 121, 93, 47, 7, 92, 11 }, TableSignatures.PinnedItemIds);
        Assert.Equal(TableSignatures.PinnedItemIds.Length, TableSignatures.PinnedItemVanillaRows.Length);
        Assert.All(TableSignatures.PinnedItemVanillaRows, r => Assert.Equal(8, r.Length));
    }

    [Fact]
    public void Each_pinned_item_has_a_baseline_that_matches_its_vanilla_row_and_a_proposed_that_differs()
    {
        for (int i = 0; i < TableSignatures.PinnedItemIds.Length; i++)
        {
            int id = TableSignatures.PinnedItemIds[i];
            var item = ItemOf(id);
            int baseline = (int)item["baseline"]!["wp"]!;
            int proposed = (int)item["proposed"]!["wp"]!;
            Assert.Equal(TableSignatures.PinnedItemVanillaRows[i][4], baseline);
            Assert.NotEqual(baseline, proposed);
        }
    }

    [Fact]
    public void No_pinned_item_has_a_wp_lane_in_meta_so_none_is_ever_a_writer_target()
    {
        var meta = JObject.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "LivingWeapon", "meta.json")));
        foreach (int id in TableSignatures.PinnedItemIds)
        {
            string lane = (string?)meta[id.ToString()]?["lane"] ?? "";
            Assert.NotEqual("wp", lane);
            Assert.NotEqual("wp+faith", lane);
        }
    }

    [Fact]
    public void Every_pinned_address_is_a_property_so_the_image_base_is_read_at_use()
    {
        var fields = typeof(TableSignatures).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(long));
        var props = typeof(TableSignatures).GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(p => p.PropertyType == typeof(long)).ToArray();
        Assert.True(props.Length >= 8);
    }

    [Fact]
    public void No_pinned_span_is_all_zero_or_shorter_than_seven_bytes()
    {
        Assert.Equal(8, TableSignatures.Sites.Count);
        foreach (var (name, addr, bytes) in TableSignatures.Sites)
        {
            Assert.True(bytes.Length >= 7, name);
            Assert.Contains(bytes, b => b != 0);
            Assert.True(addr > 0, name);
        }
    }

    [Fact]
    public void The_action_row_marker_is_four_ff_bytes()
        => Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, TableSignatures.ActionRowMarker);

    [Fact]
    public void Provokes_vanilla_pins_are_the_decoy_byte_and_a_zero_inflict_row()
    {
        Assert.Equal((byte)0x2D, ProvokePolicy.VanillaInflictIndexByAbility[189]);
        Assert.Equal(new byte[6], ProvokePolicy.VanillaInflictRow29);
    }
}
