using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// The work ledger's rules, docs/TODO.md and docs/CHANGELOG.md. Four sections in a fixed order,
/// every row in its grammar, every id used once, no em dashes, and a locked Now that names how
/// each late card got in. Now may stand empty between cards. The id prefix comes from rules.json.
/// </summary>
public class TodoContractTests
{
    private static readonly LedgerGrammar Test = new("ZZ");
    private static LedgerGrammar Repo => LedgerGrammar.ForRepo();

    [Theory]
    [InlineData("- **[ZZ-1] Some title here** (opened 2026-07-21) [QUEUED]", true)]
    [InlineData("- **[ZZ-2] Some title here** (opened 2026-07-21) [BUILDING]", true)]
    [InlineData("- **[ZZ-3] Some title here** (opened 2026-07-21) [AWAITING-LIVE]", true)]
    [InlineData("- **[ZZ-4] Some title here** (opened 2026-07-21) [BLOCKED(owner live session)]", true)]
    [InlineData("- **[ZZ-5] Some title here** (opened 2026-07-21)", false)]
    [InlineData("- **[ZZ-6] Some title here** (opened 2026-07-21) [WAITING]", false)]
    [InlineData("- **[ZZ-7] Some title here** (opened 2026-07-21) [BLOCKED]", false)]
    [InlineData("- **[ZZ-8] Some title here** (opened 2026-7-21) [QUEUED]", false)]
    [InlineData("- [ZZ-9] Some title here (opened 2026-07-21) [QUEUED]", false)]
    [InlineData("- **[ZZ-10] Title** rogue **[ZZ-11] second marker** (opened 2026-07-21) [QUEUED]", false)]
    [InlineData("- **[QQ-12] Wrong repo prefix** (opened 2026-07-21) [QUEUED]", false)]
    public void IsNowEntryLine_grammar_cases(string line, bool expected)
        => Assert.Equal(expected, Test.NowEntry.IsMatch(line));

    [Theory]
    [InlineData("- [ZZ-10] 2026-07-21: One sentence description.", true)]
    [InlineData("- [ZZ-11] 2026-07-21: ", false)]
    [InlineData("- [ZZ-12] 2026-7-21: One sentence description.", false)]
    [InlineData("- **[ZZ-13] Some title** 2026-07-21: text", false)]
    [InlineData("- ZZ-14 2026-07-21: text", false)]
    [InlineData("- [QQ-15] 2026-07-21: wrong repo prefix.", false)]
    public void IsBacklogEntryLine_grammar_cases(string line, bool expected)
        => Assert.Equal(expected, Test.BacklogEntry.IsMatch(line));

    [Theory]
    [InlineData("- [ZZ-15] SHIPPED 58d5c7b 2026-07-21: summary text.", true)]
    [InlineData("- [ZZ-16] WONTFIX 2026-07-21: summary text.", true)]
    [InlineData("- [ZZ-17] RETRACTED 2026-07-21: summary text.", true)]
    [InlineData("- [ZZ-18] SHIPPED 2026-07-21: summary text.", false)]
    [InlineData("- [ZZ-19] SHIPPED zzzzzzz 2026-07-21: summary text.", false)]
    [InlineData("- [ZZ-20] DEFERRED 2026-07-21: summary text.", false)]
    [InlineData("- [ZZ-21] SHIPPED 58d5c7b 2026-7-21: summary text.", false)]
    [InlineData("- ZZ-22 SHIPPED abc1234 2026-07-21: summary text.", false)]
    [InlineData("- [QQ-23] SHIPPED 58d5c7b 2026-07-21: wrong repo prefix.", false)]
    public void IsChangelogEntryLine_grammar_cases(string line, bool expected)
        => Assert.Equal(expected, Test.ChangelogEntry.IsMatch(line));

    [Fact]
    public void ChangelogEntryLines_catches_a_bracketless_entry_that_a_bracket_prefix_filter_would_miss()
    {
        var lines = new[]
        {
            "# Changelog (work-ledger exits)",
            "",
            "## 1.5.0 cycle",
            "",
            "- [ZZ-1] SHIPPED abc1234 2026-07-21: a well-formed entry.",
            "  continuation prose for ZZ-1.",
            "- ZZ-9 SHIPPED abc1234 2026-07-21: mangled entry missing its opening bracket.",
        };

        var scanned = Ledger.ChangelogEntryLines(lines);

        string mangled = scanned.Single(l => l.Contains("ZZ-9"));
        Assert.False(Test.ChangelogEntry.IsMatch(mangled), "the scan must hand the mangled row to the grammar, which must reject it");
    }

    [Fact]
    public void TODO_has_exactly_the_four_required_headers_in_order()
    {
        var headers = File.ReadAllLines(Ledger.TodoPath).Where(l => l.StartsWith("## ")).ToList();
        Assert.True(headers.Count == 4, $"TODO.md must have exactly 4 '## ' headers, found {headers.Count}: [{string.Join(" | ", headers)}]");
        Assert.True(LedgerGrammar.NowHeader.IsMatch(headers[0]), $"First header must match '## Now (release: ...)', found: {headers[0]}");
        Assert.True(headers[1] == "## Backlog", $"Second header must be exactly '## Backlog', found: {headers[1]}");
        Assert.True(headers[2].StartsWith("## Walled"), $"Third header must start with '## Walled', found: {headers[2]}");
        Assert.True(headers[3].StartsWith("## Format"), $"Fourth header must start with '## Format', found: {headers[3]}");
    }

    [Fact]
    public void Every_Now_entry_matches_the_grammar_and_carries_exactly_one_Done_means_and_one_Verify()
    {
        var entries = Ledger.GroupTopLevelEntries(Ledger.NowSection().Body);

        var badGrammar = new List<string>();
        var violations = new List<string>();
        foreach (var entry in entries)
        {
            string topLine = entry[0];
            if (!Repo.NowEntry.IsMatch(topLine)) badGrammar.Add(topLine);
            int doneMeans = entry.Skip(1).Count(l => l.Trim().StartsWith("- Done means:"));
            int verify = entry.Skip(1).Count(l => l.Trim().StartsWith("- Verify:"));
            if (doneMeans != 1) violations.Add($"{topLine} has {doneMeans} '- Done means:' sub-bullets (must be exactly 1)");
            if (verify != 1) violations.Add($"{topLine} has {verify} '- Verify:' sub-bullets (must be exactly 1)");
        }

        Assert.True(badGrammar.Count == 0, "Now entries failing the entry-line grammar:\n" + string.Join("\n", badGrammar));
        Assert.True(violations.Count == 0, "Now entries with the wrong '- Done means:'/'- Verify:' sub-bullet count:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Now_holds_at_most_five_cards_unless_the_list_is_locked()
    {
        var now = Ledger.NowSection();
        if (now.Body.Any(l => LedgerGrammar.LockedLine.IsMatch(l))) return;
        var entries = Ledger.GroupTopLevelEntries(now.Body);
        Assert.True(entries.Count <= 5, $"Now section holds {entries.Count} entries (cap is 5): [{string.Join(", ", entries.Select(e => e[0]))}]");
    }

    [Fact]
    public void Every_Backlog_entry_matches_the_grammar()
    {
        var backlog = Ledger.FindSection(Ledger.SplitSections(File.ReadAllLines(Ledger.TodoPath)), h => h == "## Backlog");
        Assert.True(backlog.Header is not null, "TODO.md has no '## Backlog' section to check");

        var entries = Ledger.GroupTopLevelEntries(backlog.Body);
        Assert.NotEmpty(entries);
        var badGrammar = entries.Select(e => e[0]).Where(l => !Repo.BacklogEntry.IsMatch(l)).ToList();
        Assert.True(badGrammar.Count == 0, "Backlog entries failing the entry-line grammar:\n" + string.Join("\n", badGrammar));
    }

    [Fact]
    public void Every_CHANGELOG_entry_matches_the_grammar()
    {
        Assert.True(File.Exists(Ledger.ChangelogPath), "docs/CHANGELOG.md does not exist");
        var entryLines = Ledger.ChangelogEntryLines(File.ReadAllLines(Ledger.ChangelogPath));
        Assert.NotEmpty(entryLines);
        var badGrammar = entryLines.Where(l => !Repo.ChangelogEntry.IsMatch(l)).ToList();
        Assert.True(badGrammar.Count == 0, "CHANGELOG.md entries failing the entry-line grammar:\n" + string.Join("\n", badGrammar));
    }

    [Fact]
    public void Every_id_is_unique_across_TODO_and_CHANGELOG()
    {
        Assert.True(File.Exists(Ledger.ChangelogPath), "docs/CHANGELOG.md does not exist");
        var sections = Ledger.SplitSections(File.ReadAllLines(Ledger.TodoPath));
        var ids = new List<(string Id, string Source)>();

        var now = Ledger.FindSection(sections, h => LedgerGrammar.NowHeader.IsMatch(h));
        if (now.Header is not null)
            foreach (var entry in Ledger.GroupTopLevelEntries(now.Body))
                if (Repo.NowId(entry[0]) is string id) ids.Add((id, $"TODO.md Now: {entry[0]}"));

        var backlog = Ledger.FindSection(sections, h => h == "## Backlog");
        if (backlog.Header is not null)
            foreach (var entry in Ledger.GroupTopLevelEntries(backlog.Body))
                if (Repo.BacklogId(entry[0]) is string id) ids.Add((id, $"TODO.md Backlog: {entry[0]}"));

        foreach (var line in Ledger.ChangelogEntryLines(File.ReadAllLines(Ledger.ChangelogPath)))
            if (Repo.ChangelogId(line) is string id) ids.Add((id, $"CHANGELOG.md: {line}"));

        var duplicates = ids.GroupBy(x => x.Id).Where(g => g.Count() > 1).ToList();
        Assert.True(duplicates.Count == 0, $"Duplicate {Repo.Prefix}-ids found:\n" + string.Join("\n",
            duplicates.Select(g => $"{Repo.Prefix}-{g.Key}: " + string.Join(" | ", g.Select(x => x.Source)))));
    }

    [Fact]
    public void Now_release_name_appears_in_RELEASE_SCOPE_md()
    {
        string releaseName = LedgerGrammar.NowHeader.Match(Ledger.NowSection().Header).Groups[1].Value;
        Assert.True(File.Exists(Ledger.ReleaseScopePath), "docs/RELEASE_SCOPE.md does not exist");
        Assert.True(File.ReadAllText(Ledger.ReleaseScopePath).Contains(releaseName),
            $"Now header release name '{releaseName}' does not appear in docs/RELEASE_SCOPE.md");
    }

    [Fact]
    public void Neither_TODO_nor_CHANGELOG_contains_an_em_dash_or_a_double_dash_separator()
    {
        var violations = new List<string>();
        foreach (var (name, path) in new[] { ("TODO.md", Ledger.TodoPath), ("CHANGELOG.md", Ledger.ChangelogPath) })
        {
            if (!File.Exists(path)) { violations.Add($"{name}: file does not exist"); continue; }
            int lineNo = 0;
            foreach (var line in File.ReadAllLines(path))
            {
                lineNo++;
                if (line.Contains('—') || line.Contains(" -- "))
                    violations.Add($"{name}:{lineNo}: {line}");
            }
        }
        Assert.True(violations.Count == 0, "Disallowed dash sequence found:\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("- [ZZ-9] 2026-07-21: a stray backlog-shaped row", true)]
    [InlineData("- **[ZZ-9] A stray Now-shaped row** (opened 2026-07-21) [QUEUED]", true)]
    [InlineData("  - [ZZ-9] indented stray", true)]
    [InlineData("- Backlog: entry first line `- [ZZ-<n>] YYYY-MM-DD: <one sentence>`; indented", false)]
    [InlineData("- After an in-battle Retry the glow does not come back (prose, no id).", false)]
    [InlineData("- Swords cannot get new swing visuals: the art is welded to the weapon id.", false)]
    public void EntryShapedRegex_cases(string line, bool expected)
        => Assert.Equal(expected, Test.EntryShaped.IsMatch(line));

    [Fact]
    public void No_entry_shaped_line_hides_in_Walled_or_Format()
    {
        var strays = new List<string>();
        foreach (var (header, body) in Ledger.SplitSections(File.ReadAllLines(Ledger.TodoPath)))
        {
            if (header.StartsWith("## Now") || header.StartsWith("## Backlog")) continue;
            foreach (var line in body)
                if (Repo.EntryShaped.IsMatch(line))
                    strays.Add($"{header} -> {line}");
        }
        Assert.True(strays.Count == 0, "Ledger rows found outside Now and Backlog, where no check reads them; move each or delete it:\n" + string.Join("\n", strays));
    }

    [Theory]
    [InlineData(new[] { "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]" }, 0)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]" }, 0)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]" }, 1)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]", "  - Added: yes \"take it\" (2026-10-25)" }, 0)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]", "  - Added: yes take it (2026-10-25)" }, 1)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]", "  - Added: found while testing ZZ-1" }, 0)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]", "  - Added: found while testing ZZ-9" }, 1)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]", "- **[ZZ-2] B** (opened 2026-10-24) [QUEUED]", "  - Added: hotfix port of ZZ-7" }, 0)]
    [InlineData(new[] { "Locked 2026-10-24: ZZ-1, ZZ-2", "", "- **[ZZ-1] A** (opened 2026-10-24) [QUEUED]" }, 0)]
    public void Lock_violations_cover_every_way_a_card_can_join_a_locked_list(string[] body, int expected)
        => Assert.Equal(expected, Test.LockViolations(body).Count);

    [Fact]
    public void A_card_added_to_a_locked_Now_says_how_it_got_in()
    {
        var violations = Repo.LockViolations(Ledger.NowSection().Body);
        Assert.True(violations.Count == 0,
            "Cards added to the locked Now list without an '- Added:' line (a quoted yes, found while testing a listed card, or a hotfix port):\n"
            + string.Join("\n", violations));
    }
}
