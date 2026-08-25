using System;
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
public class MetaSchemaTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();

    public void Dispose()
    {
        foreach (var t in _tempDirs) t.Dispose();
    }

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
    // LW-147: TempDir() directories are tracked and deleted in Dispose (above), not leaked.

    private string TempDir()
    {
        var t = TempDirs.Create("lw_meta_");
        _tempDirs.Add(t);
        return t.Dir;
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

    // LW-251: the WeaponPalette runtime's own schema addition. A weapon with authored bench
    // colours carries "palette"/"colors"; one with none omits both keys entirely (rather than
    // emitting a null/-1 literal), so WeaponMeta's property defaults are what an absent-key
    // weapon actually reads at runtime.
    [Fact]
    public void WeaponMeta_parses_palette_and_colors_and_defaults_absent_keys_to_sentinel()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            "{\"32\":{\"name\":\"Materia Blade\",\"wp\":8,\"cat\":\"Sword\",\"formula\":1,\"flavor\":\"f\"," +
            "\"palette\":0,\"colors\":[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15]}," +
            "\"9\":{\"name\":\"Galewind\",\"wp\":7,\"cat\":\"Knife\",\"formula\":1,\"flavor\":\"f\"}}");
        var map = MetaLoader.Load(dir);

        Assert.Equal(0, map[32].Palette);
        Assert.Equal(15, map[32].Colors!.Length);
        Assert.Equal(1, map[32].Colors![0]);
        Assert.Equal(15, map[32].Colors![14]);

        // A weapon with no authored colours never carries the keys -- Palette/Colors read their
        // property defaults (-1 / null), not a baked "no colour" literal.
        Assert.Equal(-1, map[9].Palette);
        Assert.Null(map[9].Colors);
    }

    // LW-171 "Crossfire": pins the committed bake, mirroring LivingPoachTests' precedent for
    // pinning committed generated files (poach.json). Outrider Pistol (id 71) "Gun Slinger" was
    // the sole gunSlinger-flagged weapon; Arbalest (id 79) "Crossfire" is the second. Both must
    // carry the flag at tier 3 in the real, build-generated meta.json.
    [Fact]
    public void Baked_meta_flags_both_Outrider_Pistol_and_Arbalest_as_gunSlinger_at_tier3()
    {
        var map = MetaLoader.Load(Path.GetDirectoryName(RepoMetaPath())!);

        Assert.True(map.TryGetValue(71, out var pistol), "id71 (Outrider Pistol) missing from meta.json");
        Assert.NotNull(pistol.Signature);
        Assert.True(pistol.Signature!.GunSlinger);
        Assert.Equal(3, pistol.Signature.AtTier);

        Assert.True(map.TryGetValue(79, out var arbalest), "id79 (Arbalest) missing from meta.json");
        Assert.NotNull(arbalest.Signature);
        Assert.True(arbalest.Signature!.GunSlinger);
        Assert.Equal(3, arbalest.Signature.AtTier);
        Assert.Equal("Crossfire", arbalest.Signature.DisplayLabel);
    }

    // LW-251: pins the committed bake's use of data/weapon_palette_overrides.json, mirroring the
    // gunSlinger pin above. This is the only gate that catches the generator silently ignoring
    // the overrides file: Materia Blade (id 32) must carry the OVERRIDE palette (0), not the raw
    // export's mapped palette (8, data/weapon_colors.json's own "pal" field for id 32) -- and a
    // weapon with no override row (Galewind, id 9) must keep its export palette (4) untouched.
    [Fact]
    public void Baked_meta_applies_the_Materia_Blade_palette_override_and_leaves_others_alone()
    {
        var map = MetaLoader.Load(Path.GetDirectoryName(RepoMetaPath())!);

        Assert.True(map.TryGetValue(32, out var materiaBlade), "id32 (Materia Blade) missing from meta.json");
        Assert.Equal(0, materiaBlade.Palette);   // the override (data/weapon_palette_overrides.json), not the mapped 8
        Assert.NotNull(materiaBlade.Colors);
        Assert.Equal(15, materiaBlade.Colors!.Length);

        Assert.True(map.TryGetValue(9, out var galewind), "id9 (Galewind) missing from meta.json");
        Assert.Equal(4, galewind.Palette);   // export's own "pal" -- no override row for id 9
        Assert.NotNull(galewind.Colors);
        Assert.Equal(15, galewind.Colors!.Length);
    }

    // LW-250: pins the committed bake's growth-lane keys, the "routing matches bake" half of
    // the ledger's Verify bullet made mechanical. The four missing-HP weapons (Wrathblade,
    // Muramasa, Climhazzard, Tombspire) previously grew NOTHING under the formula-driven
    // Route (Tuning.SkipFormula) -- they must now carry the baked "speed" lane. Arcanum (the
    // rider-rule MA exception) and Holy Lance (the plain-Polearm PA regression guard) pin the
    // opposite corners of the same table. LW-317 (2026-08-25): the multi-lane tokens now bake
    // to their OWN real lane -- Defender (id 33, Knight Sword) pins the real "hp" token
    // GrowthEngine.Routes' u16 MaxHp hold reads, replacing the LW-250 interim "pa" collapse.
    [Fact]
    public void RepoMeta_LanesMatchTheLockedTable()
    {
        var map = MetaLoader.Load(Path.GetDirectoryName(RepoMetaPath())!);

        foreach (int id in new[] { 27, 44, 69, 103 })
        {
            Assert.True(map.TryGetValue(id, out var m), $"id{id} missing from meta.json");
            Assert.Equal("speed", m.Lane);
        }

        Assert.True(map.TryGetValue(30, out var arcanum), "id30 (Arcanum) missing from meta.json");
        Assert.Equal("ma", arcanum.Lane);

        Assert.True(map.TryGetValue(104, out var holyLance), "id104 (Holy Lance) missing from meta.json");
        Assert.Equal("pa", holyLance.Lane);

        Assert.True(map.TryGetValue(33, out var defender), "id33 (Defender) missing from meta.json");
        Assert.Equal("hp", defender.Lane);
    }
}
