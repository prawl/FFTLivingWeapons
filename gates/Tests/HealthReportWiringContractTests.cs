using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// The gates only work if nobody has to remember them. Every build script runs the shared gates
/// in order inside their own temp folder, a release writes its health record, and the workflow
/// keeps full history. These pins go red if a call is removed or reordered.
/// </summary>
public class HealthReportWiringContractTests
{
    private static readonly string[] GateOrder =
    {
        "Enter-GateTemp", "Invoke-ManifestGate", "Invoke-FormatGate", "Invoke-UnitTestGate", "Invoke-HealthGate", "Invoke-TempLeftoverGate", "Exit-GateTemp",
    };

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot.Locate(), relative));

    [Fact]
    public void Every_build_runs_the_shared_gates_in_order()
    {
        var rules = Rules.Load();
        Assert.Contains("pipeline\\gates.ps1\"", Read(rules.Pipeline), StringComparison.Ordinal);
        var problems = new List<string>();
        foreach (var script in rules.BuildScripts)
        {
            string text = Read(script);
            int last = -1;
            foreach (var gate in GateOrder)
            {
                int at = text.IndexOf(gate, StringComparison.Ordinal);
                if (at < 0) problems.Add($"{script} never calls {gate}");
                else if (at < last) problems.Add($"{script} calls {gate} too early");
                last = Math.Max(last, at);
            }
        }
        Assert.True(problems.Count == 0, "The shared gates run in a fixed order inside Enter and Exit GateTemp:\n" + string.Join("\n", problems));
    }

    [Fact]
    public void Publish_writes_the_health_record_after_a_verified_package()
    {
        var rules = Rules.Load();
        if (string.IsNullOrEmpty(rules.PublishScript))
        {
            Assert.False(File.Exists(Path.Combine(RepoRoot.Locate(), "Publish.ps1")), "Publish.ps1 exists but rules.json names no publishScript");
            return;
        }
        string publish = Read(rules.PublishScript);
        int verified = publish.IndexOf("$exitCode = 0", StringComparison.Ordinal);
        int report = publish.IndexOf("Write-HealthReport -ModVersion $finalVersion", StringComparison.Ordinal);
        Assert.True(verified >= 0, $"{rules.PublishScript} no longer marks a verified package with $exitCode = 0.");
        Assert.True(report > verified, $"{rules.PublishScript} must call Write-HealthReport after the package passes verification.");
        Assert.Contains("health.py\" @healthArgs", publish, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = \"Stop\"", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void The_release_workflow_keeps_full_history_for_the_report()
    {
        Assert.Contains("fetch-depth: 0", Read(Rules.Load().Workflow), StringComparison.Ordinal);
    }
}
