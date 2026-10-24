using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Holds the empty catch count at zero. The regex is the one tools/health.py counts with, so a
/// catch holding only a reason comment still fails here. A swallowed error must at least count
/// itself through QuietFaults.Note, or log when it risks data.
/// </summary>
public class SilentCatchContractTests
{
    private static readonly Regex EmptyCatch = new(@"catch(\s*\([^)]*\))?\s*\{\s*(/\*[^*]*\*/)?\s*\}", RegexOptions.Compiled);

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
        throw new FileNotFoundException("repo root not found above the test bin dir");
    }

    [Fact]
    public void No_production_file_has_an_empty_catch()
    {
        string root = Path.Combine(RepoRoot(), "LivingWeapon");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("bin" + Path.DirectorySeparatorChar) || rel.StartsWith("obj" + Path.DirectorySeparatorChar)) continue;
            string text = File.ReadAllText(file);
            foreach (Match m in EmptyCatch.Matches(text))
            {
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{rel}:{line}");
            }
        }
        Assert.True(offenders.Count == 0, "Empty catches found (use QuietFaults.Note or a Warn):\n" + string.Join("\n", offenders));
    }
}
