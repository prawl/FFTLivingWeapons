using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The docs three-tier reorg's enforcement gate (docs/ top level = living contracts,
/// docs/research/ = closed journals, docs/archive/ = shipped or dead one-shots), same
/// walk-up-from-the-test-bin-dir idiom as LogContractTests.RepoRoot:
///
/// A. docs/ top level (files directly in docs\, not subdirectories) set-equals an explicit
///    allow-list of living contracts, both directions (a ratchet: adding a stray top-level doc or
///    forgetting to update the list after a rename both go red).
/// B. Every top-level doc opens with `STATUS: CONTRACT` in its first 5 lines; every
///    docs/research/*.md opens with `STATUS: JOURNAL`; every docs/archive/*.md opens with
///    `STATUS: ARCHIVED`.
/// C. The only subdirectories of docs\ holding markdown are research and archive (no stray
///    fourth tier growing unnoticed).
/// D. Every doc-path reference (`docs/X.md` or `docs\X.md`) found across the runtime, the test
///    project, the tools, the docs themselves, README.md, and data/*.json resolves to a real
///    file under the repo root. This is the safety net for the reference sweep that accompanies
///    every doc move: a stale path goes red here instead of silently rotting.
/// </summary>
public class DocsContractTests
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

    // --- A. Top-level docs allow-list ---

    private static readonly HashSet<string> AllowedTopLevelDocs = new(StringComparer.OrdinalIgnoreCase)
    {
        "CHANGELOG.md", "DESIGN.md", "DEV_TEST_RECIPES.md", "LIVE_LEDGER.md", "LOGGING.md",
        "MECHANICS.md", "PATCH_REANCHOR.md", "RELEASE_SCOPE.md", "RELIQUARY_AC.md",
        "RELIQUARY_DESIGN.md", "SMOKE_TEST_2.3.0.md", "TODO.md", "USER_FEEDBACK.md",
        "VERIFY_LIVE.md",
    };

    [Fact]
    public void Docs_top_level_holds_exactly_the_allow_listed_living_contracts()
    {
        string docsDir = Path.Combine(RepoRoot(), "docs");
        var actual = Directory.EnumerateFiles(docsDir, "*.md", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileName(f)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(actual.SetEquals(AllowedTopLevelDocs),
            "docs/ top level drifted from the living-contract allow-list. " +
            $"Present but not allow-listed (move to docs/research/ or docs/archive/): [{string.Join(", ", actual.Except(AllowedTopLevelDocs))}]. " +
            $"Allow-listed but missing (moved or renamed without updating the list?): [{string.Join(", ", AllowedTopLevelDocs.Except(actual))}].");
    }

    // --- B. Tier status stamps ---

    /// <summary>Pure check: does the text carry a `STATUS: {requiredTag}` line within its first 5
    /// lines? Line splitting normalizes CRLF so the check behaves the same on any checkout.</summary>
    internal static bool HasStatusStampWithinFirstLines(string text, string requiredTag)
    {
        string prefix = "STATUS: " + requiredTag;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int scanTo = Math.Min(5, lines.Length);
        for (int i = 0; i < scanTo; i++)
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    [Theory]
    [InlineData("# X\n\nSTATUS: CONTRACT (anything here)\n\nbody", "CONTRACT", true)]
    [InlineData("# X\n\nbody with no stamp at all\n\nmore body", "CONTRACT", false)]
    [InlineData("# X\n\n\n\n\nSTATUS: CONTRACT (too late, line 6)", "CONTRACT", false)]
    [InlineData("# X\n\nSTATUS: JOURNAL (wrong tag for a contract doc)\n\nbody", "CONTRACT", false)]
    [InlineData("STATUS: CONTRACT (no heading at all, still line 1)\n\nbody", "CONTRACT", true)]
    public void HasStatusStampWithinFirstLines_lexical_cases(string text, string tag, bool expected)
        => Assert.Equal(expected, HasStatusStampWithinFirstLines(text, tag));

    private static List<string> OffendersMissingStamp(IEnumerable<string> paths, string requiredTag)
        => paths.Where(p => !HasStatusStampWithinFirstLines(File.ReadAllText(p), requiredTag))
                .Select(p => Path.GetFileName(p)!)
                .ToList();

    [Fact]
    public void Every_top_level_doc_carries_the_CONTRACT_stamp()
    {
        string docsDir = Path.Combine(RepoRoot(), "docs");
        var offenders = OffendersMissingStamp(
            Directory.EnumerateFiles(docsDir, "*.md", SearchOption.TopDirectoryOnly), "CONTRACT");
        Assert.True(offenders.Count == 0,
            "Top-level docs missing 'STATUS: CONTRACT' in their first 5 lines:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_research_doc_carries_the_JOURNAL_stamp()
    {
        string dir = Path.Combine(RepoRoot(), "docs", "research");
        Assert.True(Directory.Exists(dir), "docs/research does not exist");
        var offenders = OffendersMissingStamp(
            Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly), "JOURNAL");
        Assert.True(offenders.Count == 0,
            "docs/research entries missing 'STATUS: JOURNAL' in their first 5 lines:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Every_archive_doc_carries_the_ARCHIVED_stamp()
    {
        string dir = Path.Combine(RepoRoot(), "docs", "archive");
        Assert.True(Directory.Exists(dir), "docs/archive does not exist");
        var offenders = OffendersMissingStamp(
            Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly), "ARCHIVED");
        Assert.True(offenders.Count == 0,
            "docs/archive entries missing 'STATUS: ARCHIVED' in their first 5 lines:\n" + string.Join("\n", offenders));
    }

    // --- C. No stray fourth tier ---

    [Fact]
    public void Only_research_and_archive_subdirectories_of_docs_hold_markdown()
    {
        string docsDir = Path.Combine(RepoRoot(), "docs");
        var allowedSubdirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "research", "archive" };
        var offenders = new List<string>();
        foreach (var mdPath in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(docsDir, mdPath);
            string[] parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Length > 1 && !allowedSubdirs.Contains(parts[0]))
                offenders.Add(rel);
        }
        Assert.True(offenders.Count == 0,
            "Markdown found under a docs/ subdirectory that isn't research or archive:\n" + string.Join("\n", offenders));
    }

    // --- D. Dead-link scan ---

    private static readonly Regex DocPathRegex = new(@"docs[/\\][A-Za-z0-9_./\\-]+\.md", RegexOptions.Compiled);

    /// <summary>Extracts every doc-path reference (`docs/X.md` prose form, a C# doc-comment's
    /// `docs/X.md`, or the backslash `docs\X.md` form) from raw text, with its 1-based line
    /// number. Slashes are NOT normalized here (callers that need repo-root resolution normalize
    /// after extraction); a bare word like "HANDOFF" never matches since the pattern requires the
    /// literal "docs" segment and a ".md" suffix. Pure and testable in isolation.</summary>
    internal static List<(int Line, string Path)> ExtractDocPathReferences(string source)
    {
        var results = new List<(int, string)>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
            foreach (Match m in DocPathRegex.Matches(lines[i]))
                results.Add((i + 1, m.Value));
        return results;
    }

    [Fact]
    public void ExtractDocPathReferences_finds_a_plain_prose_reference()
    {
        var found = ExtractDocPathReferences("See docs/DESIGN.md for the full design rationale.");
        Assert.Contains(found, r => r.Path == "docs/DESIGN.md");
    }

    [Fact]
    public void ExtractDocPathReferences_finds_a_reference_inside_a_doc_comment()
    {
        var found = ExtractDocPathReferences("/// matches docs/LOGGING.md's committed verb table one-for-one.");
        Assert.Contains(found, r => r.Path == "docs/LOGGING.md");
    }

    [Fact]
    public void ExtractDocPathReferences_finds_the_backslash_form()
    {
        var found = ExtractDocPathReferences(@"Wall context: `docs\ITEM_CAP_261_BREAK_JOURNEY.md`");
        Assert.Contains(found, r => r.Path == @"docs\ITEM_CAP_261_BREAK_JOURNEY.md");
    }

    [Fact]
    public void ExtractDocPathReferences_ignores_a_bare_word_that_is_not_a_path()
    {
        var found = ExtractDocPathReferences("root handoff.md is disposable session scratch; HANDOFF stays out of it.");
        Assert.DoesNotContain(found, r => r.Path.Contains("HANDOFF", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractDocPathReferences_finds_every_reference_on_one_line()
    {
        var found = ExtractDocPathReferences("See docs/DESIGN.md and also docs/LOGGING.md in the same breath.");
        Assert.Contains(found, r => r.Path == "docs/DESIGN.md");
        Assert.Contains(found, r => r.Path == "docs/LOGGING.md");
        Assert.Equal(2, found.Count);
    }

    /// <summary>A doc-path match on a line that also mentions "FFTHandsFree" is a cross-reference
    /// to the SIBLING repo's own docs/ folder (e.g. "FFTHandsFree `docs/BATTLE_MEMORY_MAP.md`") --
    /// this codebase's research prose always tags such a mention with that literal word on the
    /// same line, so the scan treats it as external rather than resolving it against OUR repo
    /// root.</summary>
    private static bool IsCrossRepoMention(string line) => line.Contains("FFTHandsFree", StringComparison.Ordinal);

    /// <summary>Pre-existing dangling doc-path mentions that predate the 3-tier reorg and name no
    /// path this reorg moves: a retired doc whose content was folded into MECHANICS.md, and a
    /// since-folded RELIQUARY_P1_PLAN.md absorbed into RELIQUARY_AC.md/RELIQUARY_DESIGN.md.
    /// EXACT-SET ratchet, same spirit as LogContractTests.LegacyCallers: the dead-link Fact below
    /// asserts the observed dangling set SetEquals this list, both directions, enforced by the
    /// test itself. Fixing one of these entries without removing it here fails the test exactly
    /// as hard as a brand-new dangling reference does; shrink this list the moment its dangle is
    /// fixed, and never add an entry to hide a new one.</summary>
    private static readonly HashSet<(string File, string Path)> KnownPreexistingDanglingRefs = new()
    {
        ("EarnedAnchors.cs", "docs/RELIQUARY_AC.md/RELIQUARY_P1_PLAN.md"),
        ("CardLineTests.cs", "docs/RELIQUARY_P1_PLAN.md"),
        ("StoryLinesTests.cs", "docs/RELIQUARY_P1_PLAN.md"),
        ("MECHANICS.md", "docs/UNIMPLEMENTED_MECHANICS.md"),
    };

    /// <summary>This file's own name: excluded from the scan it performs. Its doc-comments and its
    /// KnownPreexistingDanglingRefs table necessarily contain doc-path-shaped example/exemption
    /// literals (including some intentionally-dangling ones); scanning itself would be
    /// self-referential noise, not a real product/doc reference.</summary>
    private const string SelfFileName = "DocsContractTests.cs";

    /// <summary>Link TARGETS that legitimately exist only on a dev machine (gitignored, absent in
    /// a clean checkout): a reference to one is skipped rather than resolved, because its
    /// existence varies by environment and would flip this scan's verdict by machine (resolves
    /// locally, dangles in CI; the 2026-07-05 push went red exactly this way). The target-side
    /// twin of DeadLinkScanFiles' HANDOFF source exclusion below.</summary>
    private static readonly HashSet<string> EnvironmentDependentTargets = new(StringComparer.Ordinal)
    {
        "docs/archive/HANDOFF.md",
    };

    /// <summary>True if any path SEGMENT (not just a substring) equals the given directory name --
    /// a bare Contains(Path.Combine("obj", "")) also matches a folder merely ENDING in "obj" (e.g.
    /// "CustomObj\file.cs"), which this segment-aware check does not.</summary>
    private static bool HasDirSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> CsFilesUnder(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !HasDirSegment(f, "obj") && !HasDirSegment(f, "bin")
                    && Path.GetFileName(f) != SelfFileName)
            : Enumerable.Empty<string>();

    private static IEnumerable<string> DeadLinkScanFiles(string repoRoot)
    {
        var files = new List<string>();
        files.AddRange(CsFilesUnder(Path.Combine(repoRoot, "LivingWeapon")));
        files.AddRange(CsFilesUnder(Path.Combine(repoRoot, "LivingWeapon.Tests")));

        string toolsDir = Path.Combine(repoRoot, "tools");
        if (Directory.Exists(toolsDir))
        {
            files.AddRange(Directory.EnumerateFiles(toolsDir, "*.py", SearchOption.AllDirectories));
            files.AddRange(Directory.EnumerateFiles(toolsDir, "*.ps1", SearchOption.AllDirectories));
        }

        files.AddRange(Directory.EnumerateFiles(repoRoot, "*.ps1", SearchOption.TopDirectoryOnly));

        string docsDir = Path.Combine(repoRoot, "docs");
        if (Directory.Exists(docsDir))
            files.AddRange(Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories)
                // Local-only rolling session scratch doc: untracked, gitignored, absent in CI --
                // an environment-dependent surface that must not vary this scan's result by machine.
                .Where(f => Path.GetRelativePath(repoRoot, f) != Path.Combine("docs", "archive", "HANDOFF.md")));

        string readme = Path.Combine(repoRoot, "README.md");
        if (File.Exists(readme)) files.Add(readme);

        string dataDir = Path.Combine(repoRoot, "data");
        if (Directory.Exists(dataDir))
            files.AddRange(Directory.EnumerateFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly));

        return files.Distinct();
    }

    [Fact]
    public void No_doc_path_reference_in_the_repo_dangles()
    {
        string repoRoot = RepoRoot();
        var observed = new HashSet<(string File, string Path)>();
        foreach (var path in DeadLinkScanFiles(repoRoot))
        {
            string name = Path.GetFileName(path);
            string text = File.ReadAllText(path);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            foreach (var (lineNo, refPath) in ExtractDocPathReferences(text))
            {
                string line = lines[lineNo - 1];
                if (IsCrossRepoMention(line)) continue;

                string forward = refPath.Replace('\\', '/');
                if (EnvironmentDependentTargets.Contains(forward)) continue;

                string normalized = refPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                string full = Path.Combine(repoRoot, normalized);
                if (!File.Exists(full))
                    observed.Add((name, forward));
            }
        }
        // Exact-set ratchet (LogContractTests.LegacyCallers' pattern): a fixed dangle that isn't
        // shrunk from the list goes red just as hard as a brand-new, undeclared dangle does.
        Assert.True(observed.SetEquals(KnownPreexistingDanglingRefs),
            "Dangling doc-path reference set drifted from the declared ratchet list. " +
            $"newly dangling (fix or declare): [{string.Join(", ", observed.Except(KnownPreexistingDanglingRefs))}]. " +
            $"no longer dangling (SHRINK the list): [{string.Join(", ", KnownPreexistingDanglingRefs.Except(observed))}].");
    }
}
