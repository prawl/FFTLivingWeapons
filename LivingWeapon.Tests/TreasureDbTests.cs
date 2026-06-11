using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Newtonsoft.Json;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Fail-soft loader contract + structural validation for TreasureDb.
/// Three pillars: missing file → empty dataset; corrupt JSON → empty; malformed rows
/// skipped while good rows load.  Plus the address validation rules baked into the
/// loader: module span 0x140000000..0x143000000 (inclusive low, exclusive high),
/// UI-render-arena rejection 0x140C63000..0x140CC5000.
/// </summary>
public class TreasureDbTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "td_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    // ── fail-soft trio ───────────────────────────────────────────────────────

    [Fact]
    public void Load_missing_file_returns_empty_dataset_not_a_crash()
    {
        var db = TreasureDb.Load(TempDir());
        Assert.Empty(db.Maps);
    }

    [Fact]
    public void Load_corrupt_json_returns_empty_dataset_not_a_crash()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "treasure.json"), "{ this is not json");
        var db = TreasureDb.Load(dir);
        Assert.Empty(db.Maps);
    }

    [Fact]
    public void Load_valid_single_map_with_no_tiles_is_accepted()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{"mapId": 74, "name": "The Siedge Weald", "tileCount": 4,
                         "fpLen": null, "fpHash": null, "tiles": []}]
            }
            """);
        var db = TreasureDb.Load(dir);
        Assert.Single(db.Maps);
        var m = db.Maps[0];
        Assert.Equal(74, m.MapId);
        Assert.Equal("The Siedge Weald", m.Name);
        Assert.Equal(4, m.TileCount);
        Assert.Null(m.FpLen);
        Assert.Null(m.FpHash);
        Assert.Empty(m.Tiles);
    }

    // ── address parsing + validation ─────────────────────────────────────────

    [Fact]
    public void Hex_addr_string_parses_correctly()
    {
        var dir = TempDir();
        // 0x140de1ea7 is inside the module span and outside the UI arena
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{
                "mapId": 74, "name": "The Siedge Weald", "tileCount": 4,
                "fpLen": 448, "fpHash": "0xabcdef1234567890",
                "tiles": [{"x": 0, "y": 1,
                           "addrs": [["0x140de1ea7", "0x01"]]}]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        Assert.Single(db.Maps);
        Assert.Single(db.Maps[0].Tiles);
        var tile = db.Maps[0].Tiles[0];
        Assert.Equal(0, tile.X);
        Assert.Equal(1, tile.Y);
        Assert.Single(tile.Addrs);
        Assert.Equal(0x140de1ea7L, tile.Addrs[0].Addr);
        Assert.Equal(0x01, tile.Addrs[0].Off);
    }

    [Fact]
    public void Addr_below_module_span_is_rejected_row_dropped()
    {
        var dir = TempDir();
        // 0x13FFFFFFF is below 0x140000000
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{
                "mapId": 74, "name": "The Siedge Weald", "tileCount": 4,
                "fpLen": null, "fpHash": null,
                "tiles": [{"x": 0, "y": 1,
                           "addrs": [["0x13fffffff", "0x00"]]}]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        // Map still loads, tile with the bad addr is dropped
        Assert.Single(db.Maps);
        Assert.Empty(db.Maps[0].Tiles);
    }

    [Fact]
    public void Addr_above_module_span_is_rejected_row_dropped()
    {
        var dir = TempDir();
        // 0x143000000 is >= the exclusive upper bound
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{
                "mapId": 74, "name": "The Siedge Weald", "tileCount": 4,
                "fpLen": null, "fpHash": null,
                "tiles": [{"x": 0, "y": 1,
                           "addrs": [["0x143000000", "0x00"]]}]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        Assert.Single(db.Maps);
        Assert.Empty(db.Maps[0].Tiles);
    }

    [Fact]
    public void Addr_inside_UI_arena_is_rejected_row_dropped()
    {
        var dir = TempDir();
        // 0x140C63000 is the start of the UI arena -- must be rejected
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{
                "mapId": 74, "name": "The Siedge Weald", "tileCount": 4,
                "fpLen": null, "fpHash": null,
                "tiles": [{"x": 0, "y": 1,
                           "addrs": [["0x140c63000", "0x00"]]}]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        Assert.Single(db.Maps);
        Assert.Empty(db.Maps[0].Tiles);
    }

    [Fact]
    public void Valid_row_alongside_invalid_addr_row_still_loads()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{
                "mapId": 74, "name": "The Siedge Weald", "tileCount": 4,
                "fpLen": 448, "fpHash": "0x1111111111111111",
                "tiles": [
                  {"x": 0, "y": 1, "addrs": [["0x13fffffff", "0x00"]]},
                  {"x": 1, "y": 9, "addrs": [["0x140de1ea7", "0x01"]]}
                ]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        Assert.Single(db.Maps);
        Assert.Single(db.Maps[0].Tiles);
        Assert.Equal(1, db.Maps[0].Tiles[0].X);
        Assert.Equal(9, db.Maps[0].Tiles[0].Y);
    }

    // ── loaded map structure ─────────────────────────────────────────────────

    [Fact]
    public void Loaded_map_exposes_mapId_fpLen_fpHash_buildKey_and_tiles()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": {"timeDateStamp": 1718000000, "sizeOfImage": 12345},
              "maps": [{
                "mapId": 85, "name": "Mandalia Plain", "tileCount": 4,
                "fpLen": 512, "fpHash": "0xcbf29ce484222325",
                "tiles": [{"x": 4, "y": 4, "addrs": [["0x140de1ea7", "0x01"]]}]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        Assert.NotNull(db.BuildKey);
        Assert.Equal(1718000000, db.BuildKey!.TimeDateStamp);
        Assert.Equal(12345, db.BuildKey.SizeOfImage);
        var m = db.Maps[0];
        Assert.Equal(85, m.MapId);
        Assert.Equal(512, m.FpLen);
        // fpHash stored as ulong
        Assert.Equal(0xcbf29ce484222325UL, m.FpHash);
        Assert.Single(m.Tiles);
    }

    [Fact]
    public void Tile_addrs_expose_long_addr_and_byte_off()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "treasure.json"), """
            {
              "buildKey": null,
              "maps": [{
                "mapId": 74, "name": "x", "tileCount": 1,
                "fpLen": null, "fpHash": null,
                "tiles": [{"x": 0, "y": 1,
                           "addrs": [["0x140de1ea7", "0x00"], ["0x140de1f37", "0x01"]]}]
              }]
            }
            """);
        var db = TreasureDb.Load(dir);
        var addrs = db.Maps[0].Tiles[0].Addrs;
        Assert.Equal(2, addrs.Count);
        Assert.Equal(0x140de1ea7L, addrs[0].Addr);
        Assert.Equal(0x00, addrs[0].Off);
        Assert.Equal(0x140de1f37L, addrs[1].Addr);
        Assert.Equal(0x01, addrs[1].Off);
    }
}
