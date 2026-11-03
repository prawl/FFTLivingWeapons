using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModGates.Tests;

/// <summary>The shapes a ledger row may take, for one id prefix.</summary>
internal sealed class LedgerGrammar
{
    public static readonly Regex NowHeader = new(@"^## Now \(release: (.+)\)$", RegexOptions.Compiled);
    public static readonly Regex LockedLine = new(@"^Locked \d{4}-\d{2}-\d{2}: (.+)$", RegexOptions.Compiled);

    public string Prefix { get; }
    public Regex NowEntry { get; }
    public Regex BacklogEntry { get; }
    public Regex ChangelogEntry { get; }
    public Regex EntryShaped { get; }
    public Regex Added { get; }
    public Regex IdInLock { get; }

    public LedgerGrammar(string prefix)
    {
        Prefix = prefix;
        string p = Regex.Escape(prefix);
        // A title may not hold an asterisk, so a second bold marker can never be swallowed.
        NowEntry = new Regex(@"^- \*\*\[" + p + @"-(\d+)\] [^*]+\*\* \(opened \d{4}-\d{2}-\d{2}\) \[(QUEUED|BUILDING|AWAITING-LIVE|BLOCKED\([^)]+\))\]$", RegexOptions.Compiled);
        BacklogEntry = new Regex(@"^- \[" + p + @"-(\d+)\] \d{4}-\d{2}-\d{2}: .+", RegexOptions.Compiled);
        ChangelogEntry = new Regex(@"^- \[" + p + @"-(\d+)\] (SHIPPED [0-9a-f]{7,40} |WONTFIX |RETRACTED )\d{4}-\d{2}-\d{2}: .+", RegexOptions.Compiled);
        // Anything that looks like a row, at any indent, bold or not.
        EntryShaped = new Regex(@"^\s*-\s*(\*\*)?\[" + p + @"-\d+\]", RegexOptions.Compiled);
        // A quoted yes keeps the owner's own words on the row.
        Added = new Regex(@"^- Added: (yes "".+"" \(\d{4}-\d{2}-\d{2}\)|found while testing " + p + @"-(\d+)|hotfix port of " + p + @"-\d+)$", RegexOptions.Compiled);
        IdInLock = new Regex(p + @"-(\d+)", RegexOptions.Compiled);
    }

    public static LedgerGrammar ForRepo() => new(Rules.Load().TicketPrefix);

    public string? NowId(string line) => Id(NowEntry, line);
    public string? BacklogId(string line) => Id(BacklogEntry, line);
    public string? ChangelogId(string line) => Id(ChangelogEntry, line);

    private static string? Id(Regex regex, string line)
    {
        var m = regex.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Cards in a locked Now that the lock did not list and that do not say how they got in.</summary>
    public List<string> LockViolations(IReadOnlyList<string> nowBody)
    {
        var locked = nowBody.Select(l => LockedLine.Match(l)).FirstOrDefault(m => m.Success);
        if (locked is null) return new List<string>();
        var listed = IdInLock.Matches(locked.Groups[1].Value).Select(m => m.Groups[1].Value).ToHashSet();

        var violations = new List<string>();
        foreach (var entry in Ledger.GroupTopLevelEntries(nowBody))
        {
            string? id = NowId(entry[0]);
            if (id is null || listed.Contains(id)) continue;
            var added = entry.Skip(1).Select(l => Added.Match(l.Trim())).FirstOrDefault(m => m.Success);
            bool ok = added is not null && (!added.Groups[2].Success || listed.Contains(added.Groups[2].Value));
            if (!ok) violations.Add(entry[0]);
        }
        return violations;
    }
}

/// <summary>Reads docs/TODO.md and docs/CHANGELOG.md into sections and rows.</summary>
internal static class Ledger
{
    public static string TodoPath => Path.Combine(RepoRoot.Locate(), "docs", "TODO.md");
    public static string ChangelogPath => Path.Combine(RepoRoot.Locate(), "docs", "CHANGELOG.md");
    public static string ReleaseScopePath => Path.Combine(RepoRoot.Locate(), "docs", "RELEASE_SCOPE.md");

    /// <summary>Ordered (header, body) pairs split on the ## headers. Lines before the first header are dropped.</summary>
    public static List<(string Header, List<string> Body)> SplitSections(IReadOnlyList<string> lines)
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

    /// <summary>One block per row. A row starts at a column zero dash and takes every line until the next one.</summary>
    public static List<List<string>> GroupTopLevelEntries(IReadOnlyList<string> body)
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

    /// <summary>Every top level row of the changelog, so a row missing its bracket still gets judged.</summary>
    public static List<string> ChangelogEntryLines(IReadOnlyList<string> lines)
        => GroupTopLevelEntries(lines).Select(e => e[0]).ToList();

    public static (string Header, List<string> Body) FindSection(List<(string Header, List<string> Body)> sections, Func<string, bool> match)
        => sections.FirstOrDefault(s => match(s.Header));

    public static (string Header, List<string> Body) NowSection()
    {
        var now = FindSection(SplitSections(File.ReadAllLines(TodoPath)), h => LedgerGrammar.NowHeader.IsMatch(h));
        if (now.Header is null) throw new InvalidDataException("TODO.md has no '## Now (release: ...)' section");
        return now;
    }
}
