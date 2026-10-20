using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LivingWeapon/Extended/README.md is the code map for the hardest folder in the mod, and a code
/// map rots the day a file is added without a line in it. Every .cs file in the folder must be
/// named in the README, as itself or through a pattern such as ListRelocation*.cs.
/// </summary>
public class ExtendedReadmeContractTests
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

    /// <summary>True when the map names the file outright, or names its family as Stem*.cs,
    /// where Stem is the file name before its first dot (ListRelocation.Sites.cs is in
    /// the ListRelocation family).</summary>
    internal static bool Mentions(string readme, string fileName)
    {
        if (readme.Contains(fileName, StringComparison.Ordinal)) return true;
        string stem = fileName.Split('.')[0];
        return readme.Contains(stem + "*.cs", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("See ListRelocation.cs.", "ListRelocation.cs", true)]
    [InlineData("See ListRelocation*.cs.", "ListRelocation.Sites.cs", true)]
    [InlineData("See ListRelocation.cs.", "ListRelocation.Sites.cs", false)]
    [InlineData("See ThunkStub*.cs.", "ListRelocation.cs", false)]
    public void Mentions_lexical_cases(string readme, string fileName, bool expected)
        => Assert.Equal(expected, Mentions(readme, fileName));

    [Fact]
    public void Every_file_in_the_Extended_folder_is_on_the_code_map()
    {
        string folder = Path.Combine(RepoRoot(), "LivingWeapon", "Extended");
        string readme = File.ReadAllText(Path.Combine(folder, "README.md"));
        List<string> files = Directory.EnumerateFiles(folder, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(p => Path.GetFileName(p)!)
            .ToList();

        Assert.True(files.Count >= 30, $"Found only {files.Count} files in LivingWeapon/Extended; the folder path is probably wrong.");
        List<string> missing = files.Where(f => !Mentions(readme, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "LivingWeapon/Extended/README.md does not name: " + string.Join(", ", missing)
            + ". Add each to the code map, or to a Stem*.cs pattern already there.");
    }
}
