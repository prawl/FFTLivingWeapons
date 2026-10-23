using System;
using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The code health report only works if nobody has to remember it. Publish.ps1 writes the
/// release's record and every build's pipeline prints the reminder. These pins go red if either
/// call is removed, so the report cannot quietly stop being written.
/// </summary>
public class HealthReportWiringContractTests
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

    [Fact]
    public void Publish_writes_the_health_record_after_a_verified_package()
    {
        string publish = File.ReadAllText(Path.Combine(RepoRoot(), "Publish.ps1"));
        int verified = publish.IndexOf("$exitCode = 0", StringComparison.Ordinal);
        int report = publish.IndexOf("Write-HealthReport -ModVersion $finalVersion", StringComparison.Ordinal);

        Assert.True(verified >= 0, "Publish.ps1 no longer marks a verified package with $exitCode = 0.");
        Assert.True(report > verified, "Publish.ps1 must call Write-HealthReport after the package passes verification.");
        Assert.Contains("tools\\health.py\" @healthArgs", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_build_runs_the_health_selftest_and_reminder()
    {
        string pipeline = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "pipeline.ps1"));

        Assert.Contains("tools\\health.py\" --selftest", pipeline, StringComparison.Ordinal);
        Assert.Contains("tools\\health.py\" @healthStatusArgs", pipeline, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_workflow_keeps_full_history_for_the_report()
    {
        string workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("fetch-depth: 0", workflow, StringComparison.Ordinal);
    }
}
