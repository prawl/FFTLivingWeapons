using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The kill tally's persistence contract: tmp -> .bak -> move saves, .bak fallback loads,
/// and fail-safe empties. This is the player's progress file -- the paranoia is the point.
/// Each test works in its own temp directory so parallel runs never collide. Every
/// TempDir() directory is tracked and deleted in Dispose (xUnit's per-test teardown for an
/// IDisposable test class) via the shared TempDirs fixture, instead of leaking.
/// </summary>
public class KillTallyTests : IDisposable
{
    private readonly List<TempDirs> _tempDirs = new();

    private string TempDir()
    {
        var t = TempDirs.Create("lw_tally_");
        _tempDirs.Add(t);
        return t.Dir;
    }

    public void Dispose()
    {
        foreach (var t in _tempDirs) t.Dispose();
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
    public void Load_warns_naming_the_file_when_the_primary_cannot_be_read_and_still_uses_the_bak()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p + ".bak", "{\"42\":7}");
        File.WriteAllText(p, "{\"42\":9}");
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        KillTally t;
        try
        {
            using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
                t = KillTally.Load(p);
        }
        finally { ModLogger.UseNullLogger(); }
        Assert.Equal(7, t.Kills[42]);
        Assert.Contains(file.ToArray(), l => l.Contains("[WARN]") && l.Contains("kills.json") && l.Contains("IOException"));
        Assert.DoesNotContain(file.ToArray(), l => l.Contains("starts with no kills counted") || l.Contains("starting fresh"));
        Assert.DoesNotContain(file.ToArray(), l => l.Contains("next copy"));
    }

    private static List<string> CaptureWarnings(Func<KillTally> load, out KillTally tally)
    {
        var file = new List<string>();
        ModLogger.Instance = new FileConsoleLogger(_ => { }, file.Add) { LogLevel = LogLevel.Debug };
        try { tally = load(); }
        finally { ModLogger.UseNullLogger(); }
        return file;
    }

    [Fact]
    public void Load_with_both_files_unreadable_keeps_a_copy_of_each_and_says_so_plainly()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "{\"42\":9}");
        File.WriteAllText(p + ".bak", "{\"42\":7}");
        File.WriteAllText(p + ".unreadable", "older copy");
        var log = CaptureWarnings(() => KillTally.Load(p, _ => throw new IOException("locked")), out var t);
        Assert.Empty(t.Kills);
        Assert.Equal("{\"42\":9}", File.ReadAllText(p + ".unreadable.2"));
        Assert.Equal("{\"42\":7}", File.ReadAllText(p + ".bak.unreadable"));
        Assert.Equal("older copy", File.ReadAllText(p + ".unreadable"));
        Assert.Contains(log, l => l.Contains("[WARN]") && l.Contains("Your kill tally file could not be read, maybe another program has it locked, so this session starts with no kills counted. A copy of the old file was kept as ")
            && l.Contains("kills.json.unreadable.2") && l.Contains("kills.json.bak.unreadable") && l.EndsWith("rename it back to kills.json to restore your kills."));
        Assert.DoesNotContain(log, l => l.Contains("starting fresh"));
    }

    [Fact]
    public void Load_with_both_files_locked_and_no_copy_possible_says_no_copy_could_be_made()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "{\"42\":9}");
        File.WriteAllText(p + ".bak", "{\"42\":7}");
        using var a = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None);
        using var b = new FileStream(p + ".bak", FileMode.Open, FileAccess.Read, FileShare.None);
        var log = CaptureWarnings(() => KillTally.Load(p), out var t);
        Assert.Empty(t.Kills);
        Assert.Contains(log, l => l.Contains("[WARN]") && l.Contains("Your kill tally file could not be read")
            && l.Contains("No copy could be made, so close other programs and restart the game before your next battle, or the old tally will be overwritten."));
        Assert.DoesNotContain(log, l => l.Contains("starting fresh"));
    }

    [Fact]
    public void Load_with_no_files_at_all_says_starting_fresh()
    {
        var log = CaptureWarnings(() => KillTally.Load(PathIn(TempDir())), out _);
        Assert.Contains(log, l => l.Contains("No kill tally was found on disk; starting fresh."));
        Assert.DoesNotContain(log, l => l.Contains("could not be read"));
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

    // --- Tier-1: KillTally.Load wired through KillsSchema.TryParse ---

    [Fact]
    public void Load_sanitizes_negative_and_garbage_entries_and_keeps_the_valid_ones()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "{\"9\":3,\"-1\":5,\"junk\":7,\"10\":-2}");
        var t = KillTally.Load(p);
        Assert.Equal(3, t.Kills[9]);
        Assert.Single(t.Kills);
        Assert.Equal("primary", t.LoadedFrom);
    }

    [Fact]
    public void Load_falls_back_to_the_bak_when_the_primary_is_a_non_object()
    {
        var dir = TempDir();
        var p = PathIn(dir);
        File.WriteAllText(p, "[1,2,3]");                 // structurally invalid (an array, not an object)
        File.WriteAllText(p + ".bak", "{\"42\":7}");
        var t = KillTally.Load(p);
        Assert.Equal(7, t.Kills[42]);
        Assert.Equal("backup", t.LoadedFrom);
    }
}
