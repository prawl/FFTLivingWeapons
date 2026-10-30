using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A release ships only after the cold read covers every production change since the last
/// release tag. Publish.ps1 asks the shared hook before anything builds and stops on a refusal.
/// These pins read the script with its comments taken out, so they go red if the check is removed
/// or commented out, moved after the build, stops failing the package, or runs where the hook
/// cannot exist.
/// </summary>
public class ColdReadReleaseWiringContractTests
{
    private const string Check = "& python $coldReadHook release $PSScriptRoot";
    private const string Build = "Invoke-TablePipeline -FailVerb PACKAGE";

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

    // A commented out line still matches a plain text search, so the pins read only live lines.
    private static string Publish()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "Publish.ps1")).Replace("\r\n", "\n");
        text = Regex.Replace(text, "<#.*?#>", "", RegexOptions.Singleline);
        return string.Join("\n", text.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
    }

    [Fact]
    public void Publish_asks_the_cold_read_before_anything_builds()
    {
        string publish = Publish();
        int check = publish.IndexOf(Check, StringComparison.Ordinal);
        int build = publish.IndexOf(Build, StringComparison.Ordinal);

        Assert.True(check >= 0, "Publish.ps1 no longer runs the cold read release check.");
        Assert.True(build > check, "Publish.ps1 must run the cold read release check before the table pipeline builds.");
        Assert.Contains("$coldReadHook = Join-Path $HOME \".claude\\hooks\\quiz_gate.py\"", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_check_stops_the_package()
    {
        string publish = Publish();
        int check = publish.IndexOf(Check, StringComparison.Ordinal);
        int exit = check < 0 ? -1 : publish.IndexOf("if ($LASTEXITCODE -ne 0)", check, StringComparison.Ordinal);
        int stop = exit < 0 ? -1 : publish.IndexOf("throw \"REFUSING TO PACKAGE", exit, StringComparison.Ordinal);
        int build = publish.IndexOf(Build, StringComparison.Ordinal);

        Assert.True(check >= 0 && exit > check && stop > exit && build > stop,
            "Publish.ps1 must throw when the cold read release check exits non zero, before the build.");
    }

    [Fact]
    public void The_check_is_skipped_only_where_it_cannot_run()
    {
        string publish = Publish();
        int machine = publish.IndexOf("elseif (-not (Test-Path $coldReadHook))", StringComparison.Ordinal);
        int check = publish.IndexOf(Check, StringComparison.Ordinal);

        Assert.True(machine >= 0 && check > machine,
            "Publish.ps1 must skip the cold read check on a machine without the shared hooks, or the GitHub build fails.");
        Assert.Contains("if ($env:QUIZ_GATE_OFF -eq '1')", publish, StringComparison.Ordinal);
    }
}
