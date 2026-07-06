using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The TODO-ledger enforcement gate (docs/TODO.md + docs/CHANGELOG.md), same walk-up-from-the-
/// test-bin-dir idiom as LogContractTests.RepoRoot:
///
/// A. TODO.md's `## ` headers are exactly Now/Backlog/Walled/Format, in that order, no others.
/// B. Every top-level Now entry matches the bold-title/opened-date/status grammar, and carries a
///    `- Done means:` and a `- Verify:` continuation line among its indented sub-bullets.
/// C. The Now section holds at most 5 top-level entries (the hard release-focus cap).
/// D. Every top-level Backlog entry matches the id/date/sentence grammar.
/// E. Every top-level docs/CHANGELOG.md entry matches the id/disposition/date/summary grammar.
/// F. Every LW-id captured out of TODO.md's entries and CHANGELOG.md's entries is unique across
///    both files (an id may appear in exactly one place, once).
/// G. The release name captured from the Now header appears (as a substring) in
///    docs/RELEASE_SCOPE.md, so the ledger and the ship-gate doc cannot silently drift apart.
/// H. Neither file contains an em dash or a " -- " double-dash separator anywhere.
/// </summary>
public class TodoContractTests
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

    // --- Grammar regexes (pure; individually Theory-tested below) ---

    private static readonly Regex NowHeaderRegex = new(@"^## Now \(release: (.+)\)$", RegexOptions.Compiled);

    // Title capture excludes '*' outright (rather than relying on '.+?' laziness, which still
    // swallows a rogue "**[" marker whenever it is the only "** " sequence immediately preceding
    // " (opened ...)" in the line): a well-formed title never contains an asterisk, and this way
    // the regex cannot match past a second bold marker accidentally embedded in the title.
    private static readonly Regex NowEntryRegex = new(
        @"^- \*\*\[LW-(\d+)\] [^*]+\*\* \(opened \d{4}-\d{2}-\d{2}\) \[(QUEUED|BUILDING|AWAITING-LIVE|BLOCKED\([^)]+\))\]$",
        RegexOptions.Compiled);

    private static readonly Regex BacklogEntryRegex = new(
        @"^- \[LW-(\d+)\] \d{4}-\d{2}-\d{2}: .+", RegexOptions.Compiled);

    private static readonly Regex ChangelogEntryRegex = new(
        @"^- \[LW-(\d+)\] (SHIPPED [0-9a-f]{7,40} |WONTFIX |RETRACTED )\d{4}-\d{2}-\d{2}: .+",
        RegexOptions.Compiled);

    internal static bool IsNowEntryLine(string line) => NowEntryRegex.IsMatch(line);
    internal static bool IsBacklogEntryLine(string line) => BacklogEntryRegex.IsMatch(line);
    internal static bool IsChangelogEntryLine(string line) => ChangelogEntryRegex.IsMatch(line);

    private static string? NowEntryId(string line)
    {
        var m = NowEntryRegex.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? BacklogEntryId(string line)
    {
        var m = BacklogEntryRegex.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ChangelogEntryId(string line)
    {
        var m = ChangelogEntryRegex.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Splits a markdown file's lines into ordered (header, body) sections keyed on a
    /// top-level `## ` header; lines before the first such header (a title, preamble prose) are
    /// discarded since none of the checks below need them.</summary>
    internal static List<(string Header, List<string> Body)> SplitSections(IReadOnlyList<string> lines)
    {
        var sections = new List<(string Header, List<string> Body)>();
        string? header = null;
        var body = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                if (header is not null) sections.Add((header, body));
                header = line;
                body = new List<string>();
            }
            else if (header is not null)
            {
                body.Add(line);
            }
        }
        if (header is not null) sections.Add((header, body));
        return sections;
    }

    /// <summary>Groups a section's body lines into per-entry blocks: each block starts at a
    /// top-level `- ` line (column 0, no leading whitespace) and swallows every line that follows
    /// (continuation bullets and free prose alike) until the next top-level line or the section
    /// end. An indented sub-bullet like `  - Done means:` does NOT start a new group: it begins
    /// with whitespace, not `- `.</summary>
    internal static List<List<string>> GroupTopLevelEntries(IReadOnlyList<string> body)
    {
        var groups = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in body)
        {
            if (line.StartsWith("- "))
            {
                current = new List<string> { line };
                groups.Add(current);
            }
            else if (current is not null)
            {
                current.Add(line);
            }
        }
        return groups;
    }

    /// <summary>LW-21: docs/CHANGELOG.md's entry lines, scanned the same way the Backlog check
    /// scans TODO.md (every top-level `- ` line via <see cref="GroupTopLevelEntries"/>), rather
    /// than filtering for the "- [" prefix. The prefix filter let a mangled entry missing its
    /// opening bracket (say "- LW-22 SHIPPED abc1234 2026-07-05: text") dodge both the grammar
    /// check and the id-uniqueness check entirely, since it never entered the candidate set.</summary>
    private static List<string> ChangelogEntryLines(IReadOnlyList<string> lines)
        => GroupTopLevelEntries(lines).Select(e => e[0]).ToList();

    // --- Theory: Now entry-line grammar ---

    [Theory]
    [InlineData("- **[LW-1] Some title here** (opened 2026-07-05) [QUEUED]", true)]
    [InlineData("- **[LW-2] Some title here** (opened 2026-07-05) [BUILDING]", true)]
    [InlineData("- **[LW-3] Some title here** (opened 2026-07-05) [AWAITING-LIVE]", true)]
    [InlineData("- **[LW-4] Some title here** (opened 2026-07-05) [BLOCKED(owner live session)]", true)]
    [InlineData("- **[LW-5] Some title here** (opened 2026-07-05)", false)]                          // missing status token
    [InlineData("- **[LW-6] Some title here** (opened 2026-07-05) [WAITING]", false)]                 // unknown status token
    [InlineData("- **[LW-7] Some title here** (opened 2026-07-05) [BLOCKED]", false)]                 // BLOCKED needs a reason
    [InlineData("- **[LW-8] Some title here** (opened 2026-7-05) [QUEUED]", false)]                   // bad date shape
    [InlineData("- [LW-9] Some title here (opened 2026-07-05) [QUEUED]", false)]                      // missing bold markers
    [InlineData("- **[LW-10] Title** rogue **[LW-11] second marker** (opened 2026-07-05) [QUEUED]", false)]  // LW-21: greedy title must not swallow a second "**[" marker
    public void IsNowEntryLine_grammar_cases(string line, bool expected)
        => Assert.Equal(expected, IsNowEntryLine(line));

    // --- Theory: Backlog entry-line grammar ---

    [Theory]
    [InlineData("- [LW-10] 2026-07-04: One sentence description.", true)]
    [InlineData("- [LW-11] 2026-07-04: ", false)]                          // no sentence text after the colon
    [InlineData("- [LW-12] 2026-7-04: One sentence description.", false)]   // bad date shape
    [InlineData("- **[LW-13] Some title** 2026-07-04: text", false)]       // bold markers not allowed here
    [InlineData("- LW-14 2026-07-04: text", false)]                        // missing the [LW-n] brackets
    public void IsBacklogEntryLine_grammar_cases(string line, bool expected)
        => Assert.Equal(expected, IsBacklogEntryLine(line));

    // --- Theory: Changelog entry-line grammar ---

    [Theory]
    [InlineData("- [LW-15] SHIPPED 58d5c7b 2026-07-05: summary text.", true)]
    [InlineData("- [LW-16] WONTFIX 2026-07-05: summary text.", true)]
    [InlineData("- [LW-17] RETRACTED 2026-07-05: summary text.", true)]
    [InlineData("- [LW-18] SHIPPED 2026-07-05: summary text.", false)]           // SHIPPED without a hash
    [InlineData("- [LW-19] SHIPPED zzzzzzz 2026-07-05: summary text.", false)]   // hash not hex
    [InlineData("- [LW-20] DEFERRED 2026-07-05: summary text.", false)]         // unknown disposition
    [InlineData("- [LW-21] SHIPPED 58d5c7b 2026-7-05: summary text.", false)]    // bad date shape
    [InlineData("- LW-22 SHIPPED abc1234 2026-07-05: summary text.", false)]     // LW-21: missing the [LW-n] brackets
    public void IsChangelogEntryLine_grammar_cases(string line, bool expected)
        => Assert.Equal(expected, IsChangelogEntryLine(line));

    // --- LW-21: the CHANGELOG scan must see a bracketless entry, not just the grammar regex ---

    [Fact]
    public void ChangelogEntryLines_catches_a_bracketless_entry_that_a_bracket_prefix_filter_would_miss()
    {
        // Mirrors the real docs/CHANGELOG.md shape: a header/preamble, a "## ... cycle" section
        // header, a well-formed entry, then a mangled entry missing its opening "[" (LW-21's
        // reported dodge: the old scan filtered on "- [" so this line never entered the
        // candidate set at all, and so never hit the grammar or id-uniqueness checks).
        var lines = new[]
        {
            "# Changelog (work-ledger exits)",
            "",
            "## 2.3.0 cycle",
            "",
            "- [LW-1] SHIPPED abc1234 2026-07-05: a well-formed entry.",
            "  continuation prose for LW-1.",
            "- LW-22 SHIPPED abc1234 2026-07-05: mangled entry missing its opening bracket.",
        };

        var scanned = ChangelogEntryLines(lines);

        Assert.Contains(scanned, l => l.Contains("LW-22"));
        string mangled = scanned.Single(l => l.Contains("LW-22"));
        Assert.False(IsChangelogEntryLine(mangled),
            "the scan must present the mangled line to the grammar check, which must reject it");
    }

    // --- A. TODO.md section structure ---

    [Fact]
    public void TODO_has_exactly_the_four_required_headers_in_order()
    {
        string path = Path.Combine(RepoRoot(), "docs", "TODO.md");
        var headers = File.ReadAllLines(path).Where(l => l.StartsWith("## ")).ToList();
        Assert.True(headers.Count == 4,
            $"TODO.md must have exactly 4 '## ' headers, found {headers.Count}: [{string.Join(" | ", headers)}]");

        Assert.True(NowHeaderRegex.IsMatch(headers[0]),
            $"First header must match '## Now (release: ...)', found: {headers[0]}");
        Assert.True(headers[1] == "## Backlog",
            $"Second header must be exactly '## Backlog', found: {headers[1]}");
        Assert.True(headers[2].StartsWith("## Walled"),
            $"Third header must start with '## Walled', found: {headers[2]}");
        Assert.True(headers[3].StartsWith("## Format"),
            $"Fourth header must start with '## Format', found: {headers[3]}");
    }

    private static (string Header, List<string> Body) FindSection(List<(string Header, List<string> Body)> sections, Func<string, bool> match)
        => sections.FirstOrDefault(s => match(s.Header));

    private (string Header, List<string> Body) NowSection(string repoRoot)
    {
        var lines = File.ReadAllLines(Path.Combine(repoRoot, "docs", "TODO.md"));
        var sections = SplitSections(lines);
        var now = FindSection(sections, h => NowHeaderRegex.IsMatch(h));
        Assert.True(now.Header is not null, "TODO.md has no '## Now (release: ...)' section to check");
        return now;
    }

    // --- B. Now entry grammar + Done means / Verify continuation lines ---

    [Fact]
    public void Every_Now_entry_matches_the_grammar_and_carries_Done_means_and_Verify()
    {
        var now = NowSection(RepoRoot());
        var entries = GroupTopLevelEntries(now.Body);
        Assert.NotEmpty(entries);

        var badGrammar = new List<string>();
        var missingDoneMeans = new List<string>();
        var missingVerify = new List<string>();
        foreach (var entry in entries)
        {
            string topLine = entry[0];
            if (!IsNowEntryLine(topLine)) badGrammar.Add(topLine);
            if (!entry.Skip(1).Any(l => l.Trim().StartsWith("- Done means:"))) missingDoneMeans.Add(topLine);
            if (!entry.Skip(1).Any(l => l.Trim().StartsWith("- Verify:"))) missingVerify.Add(topLine);
        }

        Assert.True(badGrammar.Count == 0,
            "Now entries failing the entry-line grammar:\n" + string.Join("\n", badGrammar));
        Assert.True(missingDoneMeans.Count == 0,
            "Now entries missing a '- Done means:' continuation line:\n" + string.Join("\n", missingDoneMeans));
        Assert.True(missingVerify.Count == 0,
            "Now entries missing a '- Verify:' continuation line:\n" + string.Join("\n", missingVerify));
    }

    // --- C. Now cap ---

    [Fact]
    public void Now_section_holds_at_most_five_entries()
    {
        var now = NowSection(RepoRoot());
        var entries = GroupTopLevelEntries(now.Body);
        Assert.True(entries.Count <= 5,
            $"Now section holds {entries.Count} entries (cap is 5): [{string.Join(", ", entries.Select(e => e[0]))}]");
    }

    // --- D. Backlog entry grammar ---

    [Fact]
    public void Every_Backlog_entry_matches_the_grammar()
    {
        string repoRoot = RepoRoot();
        var lines = File.ReadAllLines(Path.Combine(repoRoot, "docs", "TODO.md"));
        var sections = SplitSections(lines);
        var backlog = FindSection(sections, h => h == "## Backlog");
        Assert.True(backlog.Header is not null, "TODO.md has no '## Backlog' section to check");

        var entries = GroupTopLevelEntries(backlog.Body);
        Assert.NotEmpty(entries);
        var badGrammar = entries.Select(e => e[0]).Where(l => !IsBacklogEntryLine(l)).ToList();
        Assert.True(badGrammar.Count == 0,
            "Backlog entries failing the entry-line grammar:\n" + string.Join("\n", badGrammar));
    }

    // --- E. Changelog entry grammar ---

    [Fact]
    public void Every_CHANGELOG_entry_matches_the_grammar()
    {
        string path = Path.Combine(RepoRoot(), "docs", "CHANGELOG.md");
        Assert.True(File.Exists(path), "docs/CHANGELOG.md does not exist");
        var entryLines = ChangelogEntryLines(File.ReadAllLines(path));
        Assert.NotEmpty(entryLines);
        var badGrammar = entryLines.Where(l => !IsChangelogEntryLine(l)).ToList();
        Assert.True(badGrammar.Count == 0,
            "CHANGELOG.md entries failing the entry-line grammar:\n" + string.Join("\n", badGrammar));
    }

    // --- F. ID uniqueness across TODO.md and CHANGELOG.md ---

    [Fact]
    public void Every_LW_id_is_unique_across_TODO_and_CHANGELOG()
    {
        string repoRoot = RepoRoot();
        var todoLines = File.ReadAllLines(Path.Combine(repoRoot, "docs", "TODO.md"));
        string changelogPath = Path.Combine(repoRoot, "docs", "CHANGELOG.md");
        Assert.True(File.Exists(changelogPath), "docs/CHANGELOG.md does not exist");
        var changelogLines = File.ReadAllLines(changelogPath);

        var sections = SplitSections(todoLines);
        var ids = new List<(string Id, string Source)>();

        var now = FindSection(sections, h => NowHeaderRegex.IsMatch(h));
        if (now.Header is not null)
            foreach (var entry in GroupTopLevelEntries(now.Body))
            {
                var id = NowEntryId(entry[0]);
                if (id is not null) ids.Add((id, $"TODO.md Now: {entry[0]}"));
            }

        var backlog = FindSection(sections, h => h == "## Backlog");
        if (backlog.Header is not null)
            foreach (var entry in GroupTopLevelEntries(backlog.Body))
            {
                var id = BacklogEntryId(entry[0]);
                if (id is not null) ids.Add((id, $"TODO.md Backlog: {entry[0]}"));
            }

        foreach (var line in ChangelogEntryLines(changelogLines))
        {
            var id = ChangelogEntryId(line);
            if (id is not null) ids.Add((id, $"CHANGELOG.md: {line}"));
        }

        var duplicates = ids.GroupBy(x => x.Id).Where(g => g.Count() > 1).ToList();
        Assert.True(duplicates.Count == 0,
            "Duplicate LW-ids found:\n" + string.Join("\n",
                duplicates.Select(g => $"LW-{g.Key}: " + string.Join(" | ", g.Select(x => x.Source)))));
    }

    // --- G. Release-name lockstep with docs/RELEASE_SCOPE.md ---

    [Fact]
    public void Now_release_name_appears_in_RELEASE_SCOPE_md()
    {
        string repoRoot = RepoRoot();
        var now = NowSection(repoRoot);
        string releaseName = NowHeaderRegex.Match(now.Header).Groups[1].Value;

        string scopePath = Path.Combine(repoRoot, "docs", "RELEASE_SCOPE.md");
        Assert.True(File.Exists(scopePath), "docs/RELEASE_SCOPE.md does not exist");
        string scopeText = File.ReadAllText(scopePath);
        Assert.True(scopeText.Contains(releaseName),
            $"Now header release name '{releaseName}' does not appear in docs/RELEASE_SCOPE.md");
    }

    // --- H. Dash rules ---

    private static readonly char EmDash = '—';

    [Fact]
    public void Neither_TODO_nor_CHANGELOG_contains_an_em_dash_or_a_double_dash_separator()
    {
        string repoRoot = RepoRoot();
        var violations = new List<string>();
        foreach (var (name, path) in new[]
        {
            ("TODO.md", Path.Combine(repoRoot, "docs", "TODO.md")),
            ("CHANGELOG.md", Path.Combine(repoRoot, "docs", "CHANGELOG.md")),
        })
        {
            if (!File.Exists(path)) { violations.Add($"{name}: file does not exist"); continue; }
            int lineNo = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                lineNo++;
                if (line.Contains(EmDash) || line.Contains(" -- "))
                    violations.Add($"{name}:{lineNo}: {line}");
            }
        }
        Assert.True(violations.Count == 0, "Disallowed dash sequence found:\n" + string.Join("\n", violations));
    }
}
