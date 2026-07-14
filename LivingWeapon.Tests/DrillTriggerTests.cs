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
        string tempDir = Path.Combine(Path.GetTempPath(), "lw_drill_" + Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, DrillTrigger.FlagName), "");

            Assert.True(DrillTrigger.DrillRequested(tempDir));
        }
        finally { Directory.Delete(tempDir, recursive: true); }
    }
}
