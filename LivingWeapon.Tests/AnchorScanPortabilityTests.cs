using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-82: AnchorScan.cs's copy-file portability contract (the FingerprintGuard.cs / HookLandmark.cs
/// precedent) enforced by source scan, same walk-up-from-the-test-bin-dir idiom as
/// LogContractTests.RepoRoot / TodoContractTests.RepoRoot. The file must reference NONE of this
/// repo's project-specific types, so a sibling mod can adopt the mechanism by copying the one file
/// rather than referencing a shared library.
/// </summary>
public class AnchorScanPortabilityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LivingWeapon", "AnchorScan.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (LivingWeapon/AnchorScan.cs) not found above the test bin dir");
    }

    private static readonly string[] ForbiddenTokens =
    {
        "Offsets", "ModLogger", "IGameMemory", "Mem.", "Flight", "Reloaded", "Barrage", "LaunchGuard",
    };

    /// <summary>Strips comment lines before the token scan: the class doc is expected to NAME the
    /// forbidden dependencies in prose (stating the contract, e.g. "no Offsets, no ModLogger..."),
    /// same as FingerprintGuard.cs's and HookLandmark.cs's own class docs do. The scan's job is
    /// catching a real code dependency (a using directive, a type reference, a member access), not
    /// flagging the sentence that documents their absence.</summary>
    private static string StripCommentLines(string source)
    {
        var kept = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//")) continue;   // both "//" and "///" doc-comment lines
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    [Fact]
    public void AnchorScan_has_zero_project_dependencies()
    {
        string path = Path.Combine(RepoRoot(), "LivingWeapon", "AnchorScan.cs");
        string code = StripCommentLines(File.ReadAllText(path));
        var found = new List<string>();
        foreach (var token in ForbiddenTokens)
            if (code.Contains(token)) found.Add(token);
        Assert.True(found.Count == 0,
            "AnchorScan.cs's portability contract was broken by a reference to: " + string.Join(", ", found));
    }
}
