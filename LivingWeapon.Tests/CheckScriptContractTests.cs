using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Check.ps1 runs every gate BuildLinked and Publish run, with one exit code and no deploy, so a
/// commit can be gated while another program has the game. The order pins read the scripts with
/// their comments taken out. The exit code tests run a copy of the real script beside a stand in
/// pipeline, so no real gate runs inside a test.
/// </summary>
public class CheckScriptContractTests : IDisposable
{
    private static readonly Regex GateCall = new(@"\bInvoke-(?:TablePipeline|\w+Gate)\b", RegexOptions.Compiled);

    private static readonly string[] Gates =
    {
        "Invoke-TablePipeline", "Invoke-ManifestGate", "Invoke-FormatGate",
        "Invoke-UnitTestGate", "Invoke-HealthGate", "Invoke-TempLeftoverGate",
    };

    private readonly List<TempDirs> _tempDirs = new();

    public void Dispose()
    {
        foreach (var t in _tempDirs) t.Dispose();
    }

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

    private static string LiveLines(string scriptName)
    {
        string path = Path.Combine(RepoRoot(), scriptName);
        Assert.True(File.Exists(path), scriptName + " is missing from the repo root.");
        string text = Regex.Replace(File.ReadAllText(path).Replace("\r\n", "\n"), "<#.*?#>", "", RegexOptions.Singleline);
        return string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
    }

    private static string[] GateCalls(string scriptName) =>
        GateCall.Matches(LiveLines(scriptName)).Select(m => m.Value).ToArray();

    [Theory]
    [InlineData("BuildLinked.ps1")]
    [InlineData("Publish.ps1")]
    public void Check_runs_the_same_gates_in_the_same_order_as(string other)
    {
        string[] theirs = GateCalls(other);

        Assert.Equal(Gates, theirs);
        Assert.Equal(theirs, GateCalls("Check.ps1"));
    }

    [Theory]
    [InlineData("RELOADEDIIMODS")]
    [InlineData("Invoke-LivingWeaponPublish")]
    [InlineData("Copy-Item")]
    [InlineData("Remove-Item")]
    [InlineData("Stop-Process")]
    [InlineData("Get-Process")]
    [InlineData("Compress-Archive")]
    [InlineData("fft_enhanced")]
    public void Check_never_deploys_packages_or_looks_at_the_game(string forbidden)
    {
        Assert.DoesNotContain(forbidden, LiveLines("Check.ps1"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_table_pipeline_takes_the_check_verb()
    {
        string pipeline = Path.Combine(RepoRoot(), "tools", "pipeline.ps1");
        string command = $". '{pipeline.Replace("'", "''")}'; " +
            "(Get-Command Invoke-TablePipeline).Parameters['FailVerb'].Attributes.ValidValues -contains 'CHECK'";

        var (code, stdout) = RunPowerShell("-Command", command);

        Assert.Equal(0, code);
        Assert.Equal("True", stdout.Trim());
    }

    [Fact]
    public void Every_gate_green_exits_zero_after_running_them_in_order()
    {
        var (code, calls) = RunCheckBesideStandIn(failAt: "");

        Assert.Equal(0, code);
        Assert.Equal(
            new[] { "Invoke-TablePipeline CHECK", "Enter-GateTemp", "Invoke-ManifestGate CHECK", "Invoke-FormatGate CHECK",
                "Invoke-UnitTestGate CHECK", "Invoke-HealthGate CHECK", "Invoke-TempLeftoverGate CHECK", "Exit-GateTemp" },
            calls);
    }

    [Theory]
    [InlineData("Invoke-TablePipeline")]
    [InlineData("Invoke-ManifestGate")]
    [InlineData("Invoke-FormatGate")]
    [InlineData("Invoke-UnitTestGate")]
    [InlineData("Invoke-HealthGate")]
    [InlineData("Invoke-TempLeftoverGate")]
    public void A_red_gate_exits_one_and_runs_no_gate_after_it(string red)
    {
        var (code, calls) = RunCheckBesideStandIn(failAt: red);

        Assert.Equal(1, code);
        string[] ran = calls.Where(c => c.StartsWith("Invoke-", StringComparison.Ordinal)).Select(c => c.Split(' ')[0]).ToArray();
        Assert.Equal(Gates.Take(Array.IndexOf(Gates, red) + 1), ran);
        Assert.Equal(calls.Contains("Enter-GateTemp"), calls.Contains("Exit-GateTemp"));
    }

    private (int Code, string[] Calls) RunCheckBesideStandIn(string failAt)
    {
        var temp = TempDirs.Create("lw_check_");
        _tempDirs.Add(temp);
        Directory.CreateDirectory(Path.Combine(temp.Dir, "tools"));
        string script = Path.Combine(temp.Dir, "Check.ps1");
        File.Copy(Path.Combine(RepoRoot(), "Check.ps1"), script);
        File.WriteAllText(Path.Combine(temp.Dir, "tools", "pipeline.ps1"), StandInPipeline(failAt));

        var (code, _) = RunPowerShell("-File", script);

        string log = Path.Combine(temp.Dir, "calls.txt");
        return (code, File.Exists(log) ? File.ReadAllLines(log) : Array.Empty<string>());
    }

    private static string StandInPipeline(string failAt)
    {
        var lines = new List<string>
        {
            "$callLog = Join-Path (Split-Path -Parent $PSScriptRoot) 'calls.txt'",
            $"$failAt = '{failAt}'",
            "function Mark([string]$name, [string]$verb) {",
            "    Add-Content -LiteralPath $callLog -Value (\"$name $verb\").Trim()",
            "    if ($name -eq $failAt) { throw \"REFUSING TO CHECK: $name is red\" }",
            "}",
            "function Enter-GateTemp { Mark 'Enter-GateTemp' ''; return @{ Dir = 'stand in' } }",
            "function Exit-GateTemp { param([hashtable]$State) Mark 'Exit-GateTemp' '' }",
        };
        foreach (string gate in Gates)
            lines.Add("function " + gate + " { param([string]$FailVerb, [hashtable]$GateTemp) Mark '" + gate + "' $FailVerb }");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static (int Code, string Stdout) RunPowerShell(string mode, string target)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", mode, target })
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(60000), "powershell.exe did not exit within the timeout");
        return (proc.ExitCode, stdout);
    }
}
