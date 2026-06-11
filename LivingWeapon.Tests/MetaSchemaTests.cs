using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Newtonsoft.Json;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The cross-language schema lockstep gate. A new signature crosses SEVEN touch points
/// (items.json -> gen_living_weapon_meta.py -> WeaponSignature JsonProperty -> the feature's
/// payload check -> Engine wiring -> tests), and Newtonsoft's default MissingMemberHandling
/// silently DROPS any json key without a matching C# property -- a key the generator emits
/// before the property exists ships an inert feature with a green suite. This test
/// deserializes the build-generated LivingWeapon/meta.json with MissingMemberHandling.Error,
/// so the drop becomes a red test. The pipeline runs gen_living_weapon_meta.py BEFORE the
/// test gate (BuildLinked/Publish/CI) precisely so this reads a fresh bake.
///
/// Also the first direct coverage of MetaLoader's fail-safe contract: a missing or corrupt
/// meta.json yields an EMPTY map (growth degrades, display paints nothing), never a crash.
/// </summary>
public class MetaSchemaTests
{
    /// <summary>Walk up from the test bin dir to the repo root (the dir holding LivingWeapon/).</summary>
    private static string RepoMetaPath()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "LivingWeapon", "meta.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LivingWeapon/meta.json not found above the test bin dir");
    }

    [Fact]
    public void Every_key_the_generator_emits_has_a_matching_property()
    {
        var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
        var map = JsonConvert.DeserializeObject<Dictionary<string, WeaponMeta>>(
            File.ReadAllText(RepoMetaPath()), settings);
        Assert.NotNull(map);
        Assert.True(map!.Count >= 100, $"meta.json holds {map.Count} weapons -- expected the full living-weapon set");
    }

    [Fact]
    public void Baked_meta_has_names_and_categories_for_every_weapon()
    {
        var map = MetaLoader.Load(Path.GetDirectoryName(RepoMetaPath())!);
        Assert.True(map.Count >= 100);
        foreach (var (id, m) in map)
        {
            Assert.False(string.IsNullOrEmpty(m.Name), $"weapon {id} has no name");
            Assert.False(string.IsNullOrEmpty(m.Cat), $"weapon {id} has no category");
        }
    }

    // --- MetaLoader's fail-safe contract (previously untested) ---

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_meta_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Load_missing_file_yields_an_empty_map_not_a_crash()
        => Assert.Empty(MetaLoader.Load(TempDir()));

    [Fact]
    public void Load_corrupt_json_yields_an_empty_map_not_a_crash()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "meta.json"), "{ this is not json");
        Assert.Empty(MetaLoader.Load(dir));
    }

    [Fact]
    public void Load_skips_non_numeric_keys_and_parses_the_rest()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            "{\"9\":{\"name\":\"Galewind\",\"wp\":7,\"cat\":\"Knife\",\"formula\":1,\"flavor\":\"f\"},\"junk\":{}}");
        var map = MetaLoader.Load(dir);
        Assert.Single(map);
        Assert.Equal("Galewind", map[9].Name);
    }
}
