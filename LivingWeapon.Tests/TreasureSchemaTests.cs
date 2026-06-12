using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Newtonsoft.Json;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Schema lockstep gate: deserializes the BUILD-GENERATED LivingWeapon/treasure.json
/// with MissingMemberHandling.Error (same pattern as MetaSchemaTests).  Any field the
/// generator emits without a matching C# property causes a hard test failure here rather
/// than silently loading as null at runtime.
///
/// Also validates the structural invariants that must hold over every shipped bake:
/// module-span addresses, valid x/y, legal mapIds, no duplicate addrs within a map,
/// stub maps have empty tiles.
/// </summary>
public class TreasureSchemaTests
{
    private const long ModuleBase = 0x140000000L;
    private const long ModuleEnd  = 0x143000000L;   // exclusive
    private const long UiArenaLo  = 0x140C63000L;
    private const long UiArenaHi  = 0x140CC5000L;   // exclusive

    private static string RepoTreasurePath()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "LivingWeapon", "treasure.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("LivingWeapon/treasure.json not found above the test bin dir");
    }

    [Fact]
    public void Every_key_the_generator_emits_has_a_matching_property()
    {
        var settings = new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error };
        var db = JsonConvert.DeserializeObject<TreasureDbJson>(
            File.ReadAllText(RepoTreasurePath()), settings);
        Assert.NotNull(db);
        Assert.NotEmpty(db!.Maps);
    }

    [Fact]
    public void All_addrs_are_within_module_span_and_outside_UI_arena()
    {
        var db = TreasureDb.Load(Path.GetDirectoryName(RepoTreasurePath())!);
        foreach (var map in db.Maps)
        foreach (var tile in map.Tiles)
        foreach (var (addr, _) in tile.Addrs)
        {
            Assert.True(addr >= ModuleBase && addr < ModuleEnd,
                $"map {map.MapId} tile ({tile.X},{tile.Y}) addr 0x{addr:x} outside module span");
            Assert.False(addr >= UiArenaLo && addr < UiArenaHi,
                $"map {map.MapId} tile ({tile.X},{tile.Y}) addr 0x{addr:x} inside UI arena");
        }
    }

    [Fact]
    public void All_coords_are_in_0_to_15()
    {
        var db = TreasureDb.Load(Path.GetDirectoryName(RepoTreasurePath())!);
        foreach (var map in db.Maps)
        foreach (var tile in map.Tiles)
        {
            Assert.InRange(tile.X, 0, 15);
            Assert.InRange(tile.Y, 0, 15);
        }
    }

    [Fact]
    public void All_mapIds_are_in_1_to_127()
    {
        var db = TreasureDb.Load(Path.GetDirectoryName(RepoTreasurePath())!);
        foreach (var map in db.Maps)
            Assert.InRange(map.MapId, 1, 127);
    }

    [Fact]
    public void No_duplicate_addrs_within_a_map()
    {
        var db = TreasureDb.Load(Path.GetDirectoryName(RepoTreasurePath())!);
        foreach (var map in db.Maps)
        {
            var seen = new HashSet<long>();
            foreach (var tile in map.Tiles)
            foreach (var (addr, _) in tile.Addrs)
                Assert.True(seen.Add(addr),
                    $"map {map.MapId}: duplicate addr 0x{addr:x}");
        }
    }

    [Fact]
    public void Stub_maps_have_empty_tiles()
    {
        var db = TreasureDb.Load(Path.GetDirectoryName(RepoTreasurePath())!);
        // A stub map has null fpHash AND is NOT map-id-only.  It must have no tiles.
        // Map-id-only maps (fpVer=0, fpHash null) legitimately carry tile addresses.
        foreach (var map in db.Maps.Where(m => m.FpHash is null && !m.IsMapIdOnly))
            Assert.Empty(map.Tiles);
    }

    [Fact]
    public void MapIdOnly_maps_have_fpVer_0_null_fpHash_and_tiles()
    {
        var db = TreasureDb.Load(Path.GetDirectoryName(RepoTreasurePath())!);
        // All map-id-only entries must have fpVer=0, null fpHash, null fpLen, and tiles present.
        foreach (var map in db.Maps.Where(m => m.IsMapIdOnly))
        {
            Assert.Equal(0, map.FpVer);
            Assert.Null(map.FpHash);
            Assert.Null(map.FpLen);
            Assert.NotEmpty(map.Tiles);
        }
    }
}

