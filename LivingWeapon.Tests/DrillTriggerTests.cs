using System;
using System.Collections.Generic;
using System.IO;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-83: the two-lane stand-down drill trigger (env variable OR marker file in the mod dir).
/// Everything except the one real-filesystem convenience-overload test goes through the injected
/// getEnv/fileExists seams, so no test ever reads or writes the real process environment.
/// </summary>
public class DrillTriggerTests
{
    private const string ModDir = @"C:\fake\mods\livingweapon";

    private static Func<string, string?> Env(string? value) => _ => value;
    private static Func<string, bool> NoFile => _ => false;

    [Fact]
    public void Env_set_to_1_with_no_file_requests_the_drill()
    {
        Assert.True(DrillTrigger.DrillRequested(ModDir, Env("1"), NoFile));
    }

    [Fact]
    public void Env_null_with_no_file_does_not_request_the_drill()
    {
        Assert.False(DrillTrigger.DrillRequested(ModDir, Env(null), NoFile));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    public void Env_values_other_than_exactly_1_do_not_request_the_drill(string value)
    {
        Assert.False(DrillTrigger.DrillRequested(ModDir, Env(value), NoFile));
    }

    [Fact]
    public void Marker_file_in_the_mod_dir_requests_the_drill_and_probes_the_exact_path()
    {
        var probed = new List<string>();
        string expectedPath = Path.Combine(ModDir, DrillTrigger.FlagName);
        Func<string, bool> fileExists = path =>
        {
            probed.Add(path);
            return path == expectedPath;
        };

        Assert.True(DrillTrigger.DrillRequested(ModDir, Env(null), fileExists));
        Assert.Contains(expectedPath, probed);
    }

    [Fact]
    public void Convenience_overload_sees_a_real_marker_file()
    {
        // The one real-filesystem test: a marker file in a fresh temp dir must trip the real
        // File.Exists lane. There is deliberately NO real-overload false-case companion: it would
        // read the real process environment, and a leaked LW_FORCE_FINGERPRINT_MISMATCH=1 on a CI
        // or dev box would flake it; the injected-seam tests above already pin the false paths.
        using var temp = TempDirs.Create("lw_drill_");
        File.WriteAllText(Path.Combine(temp.Dir, DrillTrigger.FlagName), "");

        Assert.True(DrillTrigger.DrillRequested(temp.Dir));
    }

    // --- LW-395: the second drill flag (the sensing lane's), same two lanes ---

    [Fact]
    public void Sensing_flag_env_set_to_1_requests_the_drill()
    {
        string? Get(string name) => name == DrillTrigger.SensingFlagName ? "1" : null;
        Assert.True(DrillTrigger.DrillRequested(ModDir, DrillTrigger.SensingFlagName, Get, NoFile));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.FlagName, Get, NoFile));   // the other flag is not satisfied
    }

    [Fact]
    public void Sensing_flag_marker_file_requests_the_drill_and_does_not_satisfy_the_first_flag()
    {
        string sensingPath = Path.Combine(ModDir, DrillTrigger.SensingFlagName);
        Func<string, bool> fileExists = path => path == sensingPath;

        Assert.True(DrillTrigger.DrillRequested(ModDir, DrillTrigger.SensingFlagName, Env(null), fileExists));
        Assert.False(DrillTrigger.DrillRequested(ModDir, Env(null), fileExists));   // first flag's overload: its own file is absent
    }

    [Fact]
    public void First_flag_marker_file_does_not_satisfy_the_sensing_flag()
    {
        string firstPath = Path.Combine(ModDir, DrillTrigger.FlagName);
        Func<string, bool> fileExists = path => path == firstPath;

        Assert.True(DrillTrigger.DrillRequested(ModDir, Env(null), fileExists));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.SensingFlagName, Env(null), fileExists));
    }

    [Fact]
    public void Sensing_flag_production_overload_sees_a_real_marker_file()
    {
        using var temp = TempDirs.Create("lw_drill_");
        File.WriteAllText(Path.Combine(temp.Dir, DrillTrigger.SensingFlagName), "");

        Assert.True(DrillTrigger.DrillRequested(temp.Dir, DrillTrigger.SensingFlagName));
    }

    // --- LW-392: the third drill flag (the blind-guard drill), same two lanes ---

    [Fact]
    public void Unreadable_flag_env_set_to_1_requests_the_drill_and_satisfies_no_other_flag()
    {
        string? Get(string name) => name == DrillTrigger.UnreadableFlagName ? "1" : null;
        Assert.True(DrillTrigger.DrillRequested(ModDir, DrillTrigger.UnreadableFlagName, Get, NoFile));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.FlagName, Get, NoFile));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.SensingFlagName, Get, NoFile));
    }

    [Fact]
    public void Unreadable_flag_marker_file_requests_the_drill_and_no_other_flags_file_satisfies_it()
    {
        string path = Path.Combine(ModDir, DrillTrigger.UnreadableFlagName);
        Assert.True(DrillTrigger.DrillRequested(ModDir, DrillTrigger.UnreadableFlagName, Env(null), p => p == path));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.UnreadableFlagName, Env(null),
            p => p == Path.Combine(ModDir, DrillTrigger.FlagName)));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.UnreadableFlagName, Env(null),
            p => p == Path.Combine(ModDir, DrillTrigger.SensingFlagName)));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.FlagName, Env(null), p => p == path));
        Assert.False(DrillTrigger.DrillRequested(ModDir, DrillTrigger.SensingFlagName, Env(null), p => p == path));
    }
}
