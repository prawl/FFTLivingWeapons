using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The kill tally's persistence contract: tmp -> .bak -> move saves, .bak fallback loads,
/// and fail-safe empties. This is the player's progress file -- the paranoia is the point.
/// Each test works in its own temp directory so parallel runs never collide.
/// </summary>
public class KillTallyTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_tally_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static string PathIn(string dir) => Path.Combine(dir, "kills.json");

    [Fact]
    public void Load_missing_file_yields_an_empty_tally()
    {
        var dir = TempDir();
        var t = KillTally.Load(PathIn(dir));
        Assert.Empty(t.Kills);
        Assert.Equal(0, t.Total);
    }

    [Fact]
    public void Save_then_Load_round_trips_counts_and_total()
    {
        var dir = TempDir();
        var t = KillTally.Load(PathIn(dir));
        t.Kills[9] = 3;
        t.Kills[80] = 51;
        t.Save();

        var back = KillTally.Load(PathIn(dir));
        Assert.Equal(3, back.Kills[9]);
        Assert.Equal(51, back.Kills[80]);
        Assert.Equal(54, back.Total);
    }

    [Fact]
    public void Save_leaves_no_tmp_behind_and_writes_a_bak_once_a_primary_exists()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        var t = KillTally.Load(p);
        t.Kills[1] = 1;
        t.Save();                 // first save: no primary existed, so no .bak yet
        Assert.False(File.Exists(p + ".tmp"));
        t.Kills[1] = 2;
        t.Save();                 // second save: previous primary backed up first
        Assert.True(File.Exists(p + ".bak"));
        Assert.False(File.Exists(p + ".tmp"));
        Assert.Contains("\"1\":1", File.ReadAllText(p + ".bak"));   // the bak holds the PREVIOUS state
        Assert.Contains("\"1\":2", File.ReadAllText(p));
    }

    [Fact]
    public void Load_falls_back_to_the_bak_when_the_primary_is_corrupt()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p + ".bak", "{\"42\":7}");
        File.WriteAllText(p, "{ not json");
        var t = KillTally.Load(p);
        Assert.Equal(7, t.Kills[42]);
    }

    [Fact]
    public void Load_with_both_files_corrupt_yields_an_empty_tally_not_a_crash()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "][");
        File.WriteAllText(p + ".bak", "null");
        Assert.Empty(KillTally.Load(p).Kills);
    }

    [Fact]
    public void Load_skips_non_numeric_keys_instead_of_failing()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "{\"9\":3,\"junk\":5}");
        var t = KillTally.Load(p);
        Assert.Equal(3, t.Kills[9]);
        Assert.Single(t.Kills);
    }

    [Fact]
    public void Kills_is_the_same_mutable_instance_across_the_runtime_contract()
    {
        var dir = TempDir();
        var t = KillTally.Load(PathIn(dir));
        Dictionary<int, int> shared = t.Kills;
        shared[5] = 99;                       // a subsystem credits a kill
        Assert.Equal(99, t.Kills[5]);          // the tally sees it (same instance, by design)
    }
}
