using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-134: exercises tools/pipeline.ps1's Resolve-DeployedFlavor for real, via a real Windows
/// PowerShell 5.1 child process (not a re-implementation of its logic in C#), so these tests are
/// pinned against the exact script BuildLinked.ps1's deploy guard runs, not a paraphrase of it.
/// Repo-root resolution mirrors PipelineManifestContractTests.
/// </summary>
public class DeployGuardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    private static string TempFixtureDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lw_deployguard_" + Path.GetRandomFileName());
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteFile(string dir, string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string AbsentPath(string dir, string name) => Path.Combine(dir, name);

    /// <summary>Single-quotes a path for embedding into the PowerShell command literal, doubling
    /// any embedded single quote (PowerShell's own escape for a single-quoted string) so a path
    /// can never break out of the quoting.</summary>
    private static string PsQuote(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Dot-sources the repo's real tools/pipeline.ps1, calls the real Resolve-DeployedFlavor
    /// with the scenario's paths, and returns "Flavor|Source" as printed by the child process.
    /// Uses ArgumentList (not a hand-quoted Arguments string) so .NET does the process-argument
    /// quoting; every path is independently PsQuote'd for PowerShell's own string syntax, so a temp
    /// path containing spaces can't break the command.</summary>
    private static string RunResolveDeployedFlavor(string markerPath, string stampPath, string[] tallyProbePaths)
    {
        string pipelinePath = Path.Combine(RepoRoot(), "tools", "pipeline.ps1");
        string tallyArray = string.Join(",", Array.ConvertAll(tallyProbePaths, PsQuote));
        string command =
            $". {PsQuote(pipelinePath)}; " +
            $"$v = Resolve-DeployedFlavor {PsQuote(markerPath)} {PsQuote(stampPath)} @({tallyArray}); " +
            "Write-Output ($v.Flavor + '|' + $v.Source)";

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        bool exited = proc.WaitForExit(60000);
        Assert.True(exited, "powershell.exe did not exit within the timeout");
        Assert.True(proc.ExitCode == 0, $"powershell.exe exited {proc.ExitCode}. stdout: [{stdout}] stderr: [{stderr}]");

        return stdout.Trim();
    }

    [Fact]
    public void Marker_prod_refuses_regardless_of_the_rest()
    {
        var dir = TempFixtureDir();
        string marker = WriteFile(dir, "build_flavor.txt", "prod");
        string stamp = WriteFile(dir, "run_flavor.txt", "dev");
        string tally = WriteFile(dir, "kills.json", "{}");

        string result = RunResolveDeployedFlavor(marker, stamp, new[] { tally });

        Assert.Equal("prod|marker", result);
    }

    [Fact]
    public void Marker_dev_alone_reads_dev()
    {
        var dir = TempFixtureDir();
        string marker = WriteFile(dir, "build_flavor.txt", "dev");
        string stamp = AbsentPath(dir, "run_flavor.txt");
        string tally = AbsentPath(dir, "kills.json");

        string result = RunResolveDeployedFlavor(marker, stamp, new[] { tally });

        Assert.Equal("dev|marker", result);
    }

    [Fact]
    public void A_prod_stamp_outranks_a_stale_dev_marker()
    {
        // A stale "dev" build_flavor.txt surviving a hand-extracted production zip is exactly the
        // miss class this precedence exists for: the stamp is closer to the truth (it's written by
        // the mod's own last RUN), so it wins over a marker some earlier deploy left behind.
        var dir = TempFixtureDir();
        string marker = WriteFile(dir, "build_flavor.txt", "dev");
        string stamp = WriteFile(dir, "run_flavor.txt", "prod");

        string result = RunResolveDeployedFlavor(marker, stamp, Array.Empty<string>());

        Assert.Equal("prod|stamp", result);
    }

    [Fact]
    public void A_prod_stamp_with_no_marker_reads_prod()
    {
        var dir = TempFixtureDir();
        string marker = AbsentPath(dir, "build_flavor.txt");
        string stamp = WriteFile(dir, "run_flavor.txt", "prod");

        string result = RunResolveDeployedFlavor(marker, stamp, Array.Empty<string>());

        Assert.Equal("prod|stamp", result);
    }

    // THE LOAD-BEARING TEST for the whole ticket (LW-134). A production zip install carries no
    // marker file (BuildLinked.ps1 never touched it), the mod may never have launched (so no
    // run_flavor.txt stamp either), and a kill tally sitting in the save dir with no flavour
    // evidence at all is exactly what the guard exists to protect -- the 2026-07-25 incident shape.
    // The OLD guard probed for kills.json next to the mod dir, a location LW-51 made unreachable,
    // and waved an install exactly like this one through.
    [Fact]
    public void The_2026_07_25_incident_shape_is_refused()
    {
        var dir = TempFixtureDir();
        string marker = AbsentPath(dir, "build_flavor.txt");
        string stamp = AbsentPath(dir, "run_flavor.txt");
        string tally = WriteFile(dir, "kills.json", "{\"1\":50}");

        string result = RunResolveDeployedFlavor(marker, stamp, new[] { tally });

        Assert.Equal("prod|tally", result);
    }

    [Fact]
    public void A_dev_stamp_means_the_tally_is_already_dev_tainted()
    {
        var dir = TempFixtureDir();
        string marker = AbsentPath(dir, "build_flavor.txt");
        string stamp = WriteFile(dir, "run_flavor.txt", "dev");
        string tally = WriteFile(dir, "kills.json", "{}");

        string result = RunResolveDeployedFlavor(marker, stamp, new[] { tally });

        Assert.Equal("dev|stamp", result);
    }

    [Fact]
    public void Garbage_marker_and_stamp_count_as_absent()
    {
        var dir = TempFixtureDir();
        string marker = WriteFile(dir, "build_flavor.txt", "banana");
        string stamp = WriteFile(dir, "run_flavor.txt", "   ");
        string tally = WriteFile(dir, "kills.json", "{}");

        string result = RunResolveDeployedFlavor(marker, stamp, new[] { tally });

        Assert.Equal("prod|tally", result);
    }

    [Fact]
    public void Nothing_known_and_nothing_to_protect_reads_empty()
    {
        var dir = TempFixtureDir();
        string marker = AbsentPath(dir, "build_flavor.txt");
        string stamp = AbsentPath(dir, "run_flavor.txt");
        string tally = AbsentPath(dir, "kills.json");

        string result = RunResolveDeployedFlavor(marker, stamp, new[] { tally });

        Assert.Equal("|none", result);
    }
}
