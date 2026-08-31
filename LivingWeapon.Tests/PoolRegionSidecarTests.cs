using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-324: pool_regions.json, PoolLocator's warm-start seed sidecar. Round-trip plus every
/// "come back empty, never throw" failure shape Load must tolerate: missing file, malformed
/// JSON, an unrecognized schema version, and a PE build key that does not match the running
/// build (LaunchGuard's own ExpectedTimeDateStamp/ExpectedSizeOfImage constants, referenced
/// directly here too -- never recomputed).
/// </summary>
public class PoolRegionSidecarTests
{
    [Fact]
    public void Save_then_Load_round_trips_the_region_list()
    {
        using var temp = TempDirs.Create("lw_poolregion_");
        string path = Path.Combine(temp.Dir, "pool_regions.json");
        var regions = new List<(long baseAddr, long size)> { (0x1000L, 256L), (0x9000L, 4096L) };

        PoolRegionSidecar.Save(path, regions);
        var loaded = PoolRegionSidecar.Load(path);

        Assert.Equal(2, loaded.Regions.Count);
        Assert.Contains(loaded.Regions, r => r.baseAddr == 0x1000L && r.size == 256L);
        Assert.Contains(loaded.Regions, r => r.baseAddr == 0x9000L && r.size == 4096L);
    }

    [Fact]
    public void Load_of_a_missing_file_is_empty_not_a_throw()
    {
        using var temp = TempDirs.Create("lw_poolregion_");
        string path = Path.Combine(temp.Dir, "does_not_exist.json");

        var loaded = PoolRegionSidecar.Load(path);

        Assert.Empty(loaded.Regions);
    }

    [Fact]
    public void Load_of_corrupt_json_is_empty_not_a_throw()
    {
        using var temp = TempDirs.Create("lw_poolregion_");
        string path = Path.Combine(temp.Dir, "pool_regions.json");
        File.WriteAllText(path, "{ not valid json ][");

        var loaded = PoolRegionSidecar.Load(path);

        Assert.Empty(loaded.Regions);
    }

    [Fact]
    public void Load_rejects_a_wrong_PE_key_even_with_an_otherwise_well_formed_body()
    {
        using var temp = TempDirs.Create("lw_poolregion_");
        string path = Path.Combine(temp.Dir, "pool_regions.json");
        // A well-formed file, but stamped under a PE key that is not this build's
        // (LaunchGuard.ExpectedTimeDateStamp/ExpectedSizeOfImage) -- e.g. written before a game
        // patch re-anchor. Must reject wholesale, not just the mismatched field.
        string json = "{\"version\":1,\"peTimeDateStamp\":1,\"peSizeOfImage\":2,"
                     + "\"regions\":[{\"baseAddr\":4096,\"size\":256}]}";
        File.WriteAllText(path, json);

        var loaded = PoolRegionSidecar.Load(path);

        Assert.Empty(loaded.Regions);
    }

    [Fact]
    public void Load_rejects_an_unrecognized_schema_version()
    {
        using var temp = TempDirs.Create("lw_poolregion_");
        string path = Path.Combine(temp.Dir, "pool_regions.json");
        var regions = new List<(long baseAddr, long size)> { (0x1000L, 256L) };
        PoolRegionSidecar.Save(path, regions);   // a real file, correct PE key, version 1
        string json = File.ReadAllText(path).Replace("\"version\":1", "\"version\":2");
        File.WriteAllText(path, json);

        var loaded = PoolRegionSidecar.Load(path);

        Assert.Empty(loaded.Regions);
    }
}
