using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The launch header's data sources (logging facelift stage 3): ModInfo.ReadVersion (fail-soft
/// ModConfig.json read), KillTally.LoadedFrom + its backup/fresh Warnings, and LegendStore's
/// WeaponCount/TotalMarks/LoadedFrom getters. All filesystem cases run in isolated temp dirs.
/// </summary>
public class LaunchHeaderTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_header_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    // --- ModInfo.ReadVersion ---

    [Fact]
    public void ReadVersion_reads_ModVersion_from_ModConfig_json()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "ModConfig.json"),
            "{\"ModId\":\"prawl.fft.livingweapons\",\"ModVersion\":\"2.2.2\",\"ModName\":\"FFT Living Weapons\"}");
        Assert.Equal("2.2.2", ModInfo.ReadVersion(dir));
    }

    [Fact]
    public void ReadVersion_is_unknown_when_the_file_is_missing()
        => Assert.Equal("unknown", ModInfo.ReadVersion(TempDir()));

    [Fact]
    public void ReadVersion_is_unknown_on_corrupt_json_or_missing_key()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "ModConfig.json"), "{ not json");
        Assert.Equal("unknown", ModInfo.ReadVersion(dir));

        var dir2 = TempDir();
        File.WriteAllText(Path.Combine(dir2, "ModConfig.json"), "{\"ModId\":\"x\"}");
        Assert.Equal("unknown", ModInfo.ReadVersion(dir2));
    }

    // --- KillTally.LoadedFrom + load warnings ---

    [Fact]
    public void KillTally_loaded_from_primary_reports_primary_and_warns_nothing()
    {
        var dir = TempDir();
        string path = Path.Combine(dir, "kills.json");
        File.WriteAllText(path, "{\"9\":3}");
        var lines = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(_ => { }, lines.Add);
        try
        {
            var tally = KillTally.Load(path);
            Assert.Equal("primary", tally.LoadedFrom);
            Assert.Equal(3, tally.Kills[9]);
            Assert.DoesNotContain(lines, l => l.Contains("[WARN]"));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void KillTally_falls_back_to_backup_and_warns()
    {
        var dir = TempDir();
        string path = Path.Combine(dir, "kills.json");
        File.WriteAllText(path, "{ corrupt");
        File.WriteAllText(path + ".bak", "{\"9\":7}");
        var lines = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(_ => { }, lines.Add);
        try
        {
            var tally = KillTally.Load(path);
            Assert.Equal("backup", tally.LoadedFrom);
            Assert.Equal(7, tally.Kills[9]);
            Assert.Contains(lines, l => l.Contains("[WARN]") && l.Contains("[save]") && l.Contains("backup"));
        }
        finally { ModLogger.Instance = prior; }
    }

    [Fact]
    public void KillTally_starts_fresh_and_warns_when_nothing_is_readable()
    {
        var dir = TempDir();
        string path = Path.Combine(dir, "kills.json");
        var lines = new List<string>();
        var prior = ModLogger.Instance;
        ModLogger.Instance = new FileConsoleLogger(_ => { }, lines.Add);
        try
        {
            var tally = KillTally.Load(path);
            Assert.Equal("fresh", tally.LoadedFrom);
            Assert.Empty(tally.Kills);
            Assert.Contains(lines, l => l.Contains("[WARN]") && l.Contains("[save]") && l.Contains("fresh"));
        }
        finally { ModLogger.Instance = prior; }
    }

    // --- LegendStore counters + LoadedFrom ---

    [Fact]
    public void LegendStore_counts_weapons_with_deeds_and_total_marks()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);
        Assert.Equal("fresh", store.LoadedFrom);
        Assert.Equal(0, store.WeaponCount);
        Assert.Equal(0, store.TotalMarks);

        // Two weapons with deeds; ProdMarkThresholds[0]=25 kills of one archetype earns a Mark.
        var victim = new VictimSnapshot(true, 100, 77, false);   // job 77 (Archer) -> Human
        for (int i = 0; i < Tuning.MarkThresholds[0]; i++) store.RecordDeed(9, victim);
        store.RecordDeed(52, victim);
        Assert.Equal(2, store.WeaponCount);
        Assert.Equal(1, store.TotalMarks);
    }

    [Fact]
    public void LegendStore_reports_primary_after_a_round_trip()
    {
        var dir = TempDir();
        var store = LegendStore.Load(dir);
        store.RecordDeed(9, new VictimSnapshot(true, 100, 77, false));
        store.SaveIfDirty();
        var reloaded = LegendStore.Load(dir);
        Assert.Equal("primary", reloaded.LoadedFrom);
        Assert.Equal(1, reloaded.WeaponCount);
    }

    // --- LaunchHeader composers (LW-22: the header lines must pluralize their counts) ---

    [Fact]
    public void Tally_line_singular_counts_read_singular()
        => Assert.Equal(
            "The kill tally holds 1 lifetime kill across 1 weapon (kills.json, primary).",
            LaunchHeader.ComposeTally(1, 1, "primary"));

    [Fact]
    public void Tally_line_plural_counts_read_plural()
        => Assert.Equal(
            "The kill tally holds 63 lifetime kills across 12 weapons (kills.json, primary).",
            LaunchHeader.ComposeTally(63, 12, "primary"));

    [Fact]
    public void Tally_line_zero_counts_read_plural()
        => Assert.Equal(
            "The kill tally holds 0 lifetime kills across 0 weapons (kills.json, fresh).",
            LaunchHeader.ComposeTally(0, 0, "fresh"));

    [Fact]
    public void Legends_line_singular_counts_read_singular()
        => Assert.Equal(
            "The legends hold deeds for 1 weapon and 1 Mark (legends.json, primary).",
            LaunchHeader.ComposeLegends(1, 1, "primary"));

    [Fact]
    public void Legends_line_plural_counts_read_plural()
        => Assert.Equal(
            "The legends hold deeds for 4 weapons and 2 Marks (legends.json, primary).",
            LaunchHeader.ComposeLegends(4, 2, "primary"));
}
