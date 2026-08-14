using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// tools/pipeline.ps1's $RequiredModFiles is the required-file manifest shared by
/// Publish.ps1's Verify-Package, BuildLinked.ps1's deploy verification, and CI: a package that
/// silently lost a shipped file would otherwise pass every verifier. This is the tripwire: every
/// file that actually lives under mod/FFTIVC/tables/enhanced/*.xml and
/// mod/FFTIVC/data/enhanced/nxd/*.nxd (parked bloodpact tables excluded, since those must never
/// ship) must appear somewhere in the parsed manifest. The manifest legitimately also lists
/// build outputs (LivingWeapon.dll, meta.json, poach.json) that are not in mod/, so the
/// comparison is subset-only, not a set-equals ratchet.
/// </summary>
public class PipelineManifestContractTests
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

    // LW-148: anchored to a close-paren that STARTS its own line (RegexOptions.Multiline's ^),
    // matching how tools/pipeline.ps1 actually formats the array's closing ")". The old regex's
    // non-greedy "\)" ended the match at the FIRST close-paren found ANYWHERE, including one typed
    // inside a comment -- every entry below such a comment silently vanished from the parsed
    // manifest, invisibly (the array itself, and the file, are both still perfectly valid
    // PowerShell). The array literal used to carry a hand-written warning about this; the fix
    // below makes the warning unnecessary, so it was deleted from tools/pipeline.ps1 in the same
    // commit as this test.
    private static readonly Regex RequiredModFilesBlockRegex =
        new(@"\$RequiredModFiles\s*=\s*@\(([\s\S]*?)^\)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex QuotedEntryRegex = new("\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>Extracts the quoted string entries from tools/pipeline.ps1's
    /// "$RequiredModFiles = @( ... )" array literal. The block body holds only quoted strings and
    /// # comments, so a plain quote scan over the captured block is enough; no PowerShell parser
    /// needed -- PROVIDED comment tails are stripped first (LW-148), else a quote character typed
    /// inside a comment would be scanned as if it opened/closed a real array entry.</summary>
    internal static List<string> ParseRequiredModFiles(string pipelineScriptText)
    {
        var blockMatch = RequiredModFilesBlockRegex.Match(pipelineScriptText);
        Assert.True(blockMatch.Success, "$RequiredModFiles = @( ... ) block not found in tools/pipeline.ps1");

        string body = StripCommentTails(blockMatch.Groups[1].Value);
        return QuotedEntryRegex.Matches(body).Select(m => m.Groups[1].Value).ToList();
    }

    /// <summary>Removes everything from a line's first #-outside-quotes onward, line by line
    /// (LW-148). Handles both a whole comment line and a trailing "value", # comment tail; a '#'
    /// inside an open pair of quotes is left alone (none of this manifest's entries need one, but
    /// the scan should not assume that).</summary>
    private static string StripCommentTails(string psArrayBody)
    {
        var lines = psArrayBody.Replace("\r\n", "\n").Split('\n');
        for (int li = 0; li < lines.Length; li++)
        {
            string line = lines[li];
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuotes = !inQuotes;
                else if (line[i] == '#' && !inQuotes)
                {
                    lines[li] = line.Substring(0, i);
                    break;
                }
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>Every real shipped table/nxd file, mapped to the manifest's forward-slash-relative
    /// form, excluding parked bloodpact tables (they must never ship).</summary>
    private static List<string> ExpectedShippedDataFiles(string repoRoot)
    {
        string modRoot = Path.Combine(repoRoot, "mod");
        var patterns = new (string Dir, string Glob)[]
        {
            (Path.Combine(modRoot, "FFTIVC", "tables", "enhanced"), "*.xml"),
            (Path.Combine(modRoot, "FFTIVC", "data", "enhanced", "nxd"), "*.nxd"),
        };

        var expected = new List<string>();
        foreach (var (dir, glob) in patterns)
        {
            foreach (var path in Directory.EnumerateFiles(dir, glob, SearchOption.TopDirectoryOnly))
            {
                if (path.EndsWith(".bloodpact_parked", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = Path.GetRelativePath(modRoot, path).Replace('\\', '/');
                expected.Add(rel);
            }
        }
        return expected;
    }

    [Fact]
    public void RequiredModFiles_lists_every_shipped_table_and_nxd_file()
    {
        string repoRoot = RepoRoot();
        string pipelineText = File.ReadAllText(Path.Combine(repoRoot, "tools", "pipeline.ps1"));

        var manifest = ParseRequiredModFiles(pipelineText);
        Assert.True(manifest.Count >= 10,
            $"Parsed manifest has only {manifest.Count} entries; the regex probably failed to find the real array (vacuous pass guard).");

        var expected = ExpectedShippedDataFiles(repoRoot);
        var manifestSet = new HashSet<string>(manifest, StringComparer.Ordinal);
        var missing = expected.Where(e => !manifestSet.Contains(e)).ToList();

        Assert.True(missing.Count == 0,
            "tools/pipeline.ps1's $RequiredModFiles is missing shipped file(s): " + string.Join(", ", missing));
    }

    [Fact]
    public void Release_workflow_names_JobData_and_excludes_JobCommandData()
    {
        // LW-77: JobCommandData.xml was deleted (whole-row writeback collision, see
        // JobCommandXmlContractTests); release.yml must no longer reference the retired file.
        string repoRoot = RepoRoot();
        string releaseYml = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "release.yml"));

        Assert.Contains("JobData.xml", releaseYml, StringComparison.Ordinal);
        Assert.DoesNotContain("JobCommandData.xml", releaseYml, StringComparison.Ordinal);
    }
}
