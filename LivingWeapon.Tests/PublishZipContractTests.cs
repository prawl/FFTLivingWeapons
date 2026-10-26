using System;
using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A zip built on Windows PowerShell 5.1 with ZipFile.CreateFromDirectory stores backslashes in
/// its entry names, and Linux unzip then spills loose files instead of the mod folder. These pins
/// keep Publish.ps1 writing forward slash entries and refusing a zip that still has a backslash.
/// </summary>
public class PublishZipContractTests
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

    private static string Publish() => File.ReadAllText(Path.Combine(RepoRoot(), "Publish.ps1"));

    [Fact]
    public void Publish_never_builds_the_zip_with_CreateFromDirectory()
        => Assert.DoesNotContain("CreateFromDirectory(", Publish(), StringComparison.Ordinal);

    [Fact]
    public void Publish_writes_each_entry_by_hand_with_forward_slashes()
    {
        string publish = Publish();
        Assert.Contains("CreateEntryFromFile(", publish, StringComparison.Ordinal);
        Assert.Contains(@"""$wrapperName/"" + ($relative -replace '\\', '/')", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_package_refuses_backslash_entries_before_it_normalizes_them()
    {
        string publish = Publish();
        int gate = publish.IndexOf(@"$_.FullName -match '\\'", StringComparison.Ordinal);
        int normalize = publish.IndexOf(@"$_.FullName -replace '\\', '/'", StringComparison.Ordinal);

        Assert.True(gate >= 0, "Verify-Package no longer checks raw entry names for a backslash.");
        Assert.True(normalize > gate, "The backslash check must run before entry names are normalized, or it can never fire.");
    }
}
