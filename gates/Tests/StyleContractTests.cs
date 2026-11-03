using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// Layout held mechanically. Python under tools keeps two blank lines above each top level
/// definition and never three in a row, C# never holds two in a row, no doc or production file
/// grows its count of overlong lines past its frozen number, no crash dump sits in the tree, no
/// em dash sits inside the folders and files the repo lists for that ban, and the repo's style
/// and line ending files match the rulebook's byte for byte.
/// </summary>
public class StyleContractTests
{
    private const int DocWidth = 100;
    private const int CommentWidth = 100;

    private static readonly Regex TopLevelDef = new(@"^(def |async def |class |@)", RegexOptions.Compiled);
    private static readonly HashSet<string> PrunedDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".claude", "__pycache__", "gates", "TestResults" };

    private sealed record StyleBaseline(Dictionary<string, int> Docs, Dictionary<string, int> Comments);

    private static StyleBaseline LoadBaseline()
    {
        string path = Rules.Load().BaselinePath("style.json");
        if (!File.Exists(path)) return new StyleBaseline(new(), new());
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<StyleBaseline>(File.ReadAllText(path), options) ?? new StyleBaseline(new(), new());
    }

    private static IEnumerable<string> Walk(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) yield break;
        var pruned = new HashSet<string>(PrunedDirs.Concat(Rules.Load().PrunedDirs), StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(dir, pattern)) yield return f;
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!pruned.Contains(Path.GetFileName(d)))
                foreach (var f in Walk(d, pattern)) yield return f;
    }

    private static string Rel(string path) => Path.GetRelativePath(RepoRoot.Locate(), path).Replace('\\', '/');
    private static string[] Lines(string path) => File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
    private static bool Blank(string line) => line.Trim().Length == 0;

    internal static List<string> PythonLayout(string[] lines)
    {
        var found = new List<string>();
        int run = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            run = Blank(lines[i]) ? run + 1 : 0;
            if (run == 3) found.Add($"{i + 1}: three blank lines in a row");
            if (!TopLevelDef.IsMatch(lines[i]) || (i > 0 && lines[i - 1].StartsWith('@'))) continue;
            int j = i - 1, gap = 0, longest = 0;
            for (; j >= 0 && (Blank(lines[j]) || lines[j].TrimStart().StartsWith('#')); j--)
            {
                gap = Blank(lines[j]) ? gap + 1 : 0;
                longest = Math.Max(longest, gap);
            }
            if (j >= 0 && longest < 2) found.Add($"{i + 1}: a top level definition with fewer than two blank lines above it");
        }
        return found;
    }

    internal static List<string> CSharpBlankRuns(string[] lines)
    {
        var found = new List<string>();
        for (int i = 1; i < lines.Length - 1; i++)
            if (Blank(lines[i]) && Blank(lines[i - 1])) found.Add($"{i + 1}: two blank lines in a row");
        return found;
    }

    internal static int OverlongProse(string[] lines, Regex ledgerHead)
    {
        int count = 0;
        bool fenced = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
            if (!fenced && line.Length > DocWidth && !line.TrimStart().StartsWith('|') && !ledgerHead.IsMatch(line)) count++;
        }
        return count;
    }

    internal static int OverlongComments(string[] lines) => lines.Count(l => l.TrimStart().StartsWith("//") && l.Length > CommentWidth);

    private static Regex LedgerHead(string prefix) => new(@"^- \*\*\[" + Regex.Escape(prefix) + @"-\d+\] .*\(opened \d{4}-\d{2}-\d{2}\)", RegexOptions.Compiled);

    /// <summary>The style baseline is hand written, so the refusal names the file, the key and the row to add.</summary>
    internal static string FreezeHint(string key) => $"Baseline: {Rules.Load().Baselines}/style.json, key \"{key}\", hand written; no command moves it. Wrap the line, or add today's count under that key.";

    [Theory]
    [InlineData("x = 1\n\n\ndef f():\n    pass\n", "")]
    [InlineData("x = 1\n\ndef f():\n    pass\n", "3: a top level definition with fewer than two blank lines above it")]
    [InlineData("import os\ndef f():\n    pass\n", "2: a top level definition with fewer than two blank lines above it")]
    [InlineData("x = 1\n\n\n# ---- banner ----\n\ndef f():\n    pass\n", "")]
    [InlineData("x = 1\n\n\n@wrap\ndef f():\n    pass\n", "")]
    [InlineData("x = 1\n\n@wrap\ndef f():\n    pass\n", "3: a top level definition with fewer than two blank lines above it")]
    [InlineData("\"\"\"Module.\"\"\"\n\n\n\nclass A:\n    def m(self):\n        pass\n", "4: three blank lines in a row")]
    [InlineData("class A:\n    def m(self):\n        pass\n\n    def n(self):\n        pass\n", "")]
    [InlineData("def f():\n    pass\n", "")]
    public void The_python_layout_check_names_each_slip(string source, string problem)
    {
        var expected = problem.Length == 0 ? new List<string>() : new List<string> { problem };
        Assert.Equal(expected, PythonLayout(source.Split('\n')));
    }

    [Theory]
    [InlineData("a\n\nb\n", "")]
    [InlineData("a\n\n\nb\n", "3: two blank lines in a row")]
    [InlineData("a\n   \n\t\nb\n", "3: two blank lines in a row")]
    public void The_csharp_blank_line_check_names_each_run(string source, string problem)
    {
        var expected = problem.Length == 0 ? new List<string>() : new List<string> { problem };
        Assert.Equal(expected, CSharpBlankRuns(source.Split('\n')));
    }

    [Fact]
    public void The_width_counts_skip_tables_fenced_code_and_ledger_heads_but_not_prose_or_headings()
    {
        string wide = new('w', DocWidth + 1);
        string[] doc =
        {
            wide, "# " + wide, "| " + wide, "```", wide, "```",
            "- **[ZZ-9] " + wide + "** (opened 2026-10-26) [QUEUED]", "  - Done means: " + wide, new('n', DocWidth),
        };
        Assert.Equal(3, OverlongProse(doc, LedgerHead("ZZ")));
        Assert.Equal(1, OverlongComments(new[] { "    // " + new string('c', CommentWidth), "    // short", "    " + new string('x', CommentWidth + 5) }));
    }

    [Fact]
    public void Python_under_tools_keeps_two_blank_lines_above_each_top_level_definition_and_never_three()
    {
        var folders = new[] { "tools" }.Concat(Rules.Load().PythonProd.Select(f => f.TrimEnd('/'))).Distinct(StringComparer.OrdinalIgnoreCase);
        var offenders = folders.SelectMany(d => Walk(Path.Combine(RepoRoot.Locate(), d), "*.py")).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => PythonLayout(Lines(f)).Select(p => $"{Rel(f)}:{p}")).ToList();
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void CSharp_never_holds_two_blank_lines_in_a_row()
    {
        var rules = Rules.Load();
        var offenders = new[] { rules.ProdDir, rules.TestsDir, Path.Combine(RepoRoot.Locate(), "tools") }
            .SelectMany(d => Walk(d, "*.cs"))
            .SelectMany(f => CSharpBlankRuns(Lines(f)).Select(p => $"{Rel(f)}:{p}")).ToList();
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    [Fact]
    public void No_doc_grows_its_count_of_prose_lines_over_the_doc_width()
    {
        string root = RepoRoot.Locate();
        var head = LedgerHead(Rules.Load().TicketPrefix);
        var baseline = LoadBaseline().Docs;
        var docs = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md").Where(f => Path.GetFileName(f) != "HEALTH.md").Append(Path.Combine(root, "README.md")).Where(File.Exists);
        var grown = docs.Select(f => (Doc: Rel(f), Count: OverlongProse(Lines(f), head))).Where(d => d.Count > baseline.GetValueOrDefault(d.Doc)).ToList();
        Assert.True(grown.Count == 0, $"Prose over {DocWidth} characters. {FreezeHint("docs")} Over the width: " +
            string.Join("; ", grown.Select(d => $"\"{d.Doc}\": {d.Count} (frozen at {baseline.GetValueOrDefault(d.Doc)})")));
    }

    [Fact]
    public void No_production_file_grows_its_count_of_comment_lines_over_the_comment_width()
    {
        var baseline = LoadBaseline().Comments;
        var grown = Walk(Rules.Load().ProdDir, "*.cs").Select(f => (File: Path.GetFileName(f), Count: OverlongComments(Lines(f))))
            .Where(c => c.Count > baseline.GetValueOrDefault(c.File)).ToList();
        Assert.True(grown.Count == 0, $"Comment lines over {CommentWidth} characters. {FreezeHint("comments")} Over the width: " +
            string.Join("; ", grown.Select(c => $"\"{c.File}\": {c.Count} (frozen at {baseline.GetValueOrDefault(c.File)})")));
    }

    /// <summary>A scope entry is a file, or a folder whose every text file is read; a file holding a NUL byte is binary and skipped.</summary>
    private static IEnumerable<string> ScopeFiles(string entry)
    {
        string path = Path.Combine(RepoRoot.Locate(), entry.TrimEnd('/'));
        if (File.Exists(path)) return new[] { path };
        if (!Directory.Exists(path)) return Enumerable.Empty<string>();
        return Walk(path, "*").Where(f => !File.ReadAllBytes(f).Contains((byte)0));
    }

    internal static List<int> EmDashLines(string[] lines)
        => lines.Select((l, i) => (Line: l, At: i + 1)).Where(p => p.Line.Contains('—')).Select(p => p.At).ToList();

    [Theory]
    [InlineData("a hyphen - here\nand -- a double\n", "")]
    [InlineData("an em dash — here\nplain\nand — again\n", "1,3")]
    public void Em_dash_lines_are_named_and_a_plain_hyphen_passes(string text, string expected)
        => Assert.Equal(expected, string.Join(",", EmDashLines(text.Split('\n'))));

    [Fact]
    public void No_em_dash_inside_the_folders_the_repo_lists_for_the_ban()
    {
        var hits = Rules.Load().EmDashScope.SelectMany(ScopeFiles).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => EmDashLines(Lines(f)).Select(n => $"{Rel(f)}:{n}")).ToList();
        Assert.True(hits.Count == 0, "An em dash inside the repo's em dash scope. rules.json emDashScope names the folders and files the ban covers; "
            + "write a comma or a period instead, or narrow the list to keep a data file out:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void No_crash_dump_sits_in_the_working_tree()
    {
        var dumps = Walk(RepoRoot.Locate(), "*.stackdump").Select(Rel).ToList();
        Assert.True(dumps.Count == 0, "delete the crash dumps: " + string.Join(", ", dumps));
    }

    [Theory]
    [InlineData(".editorconfig")]
    [InlineData(".gitattributes")]
    public void The_repo_copy_of_a_shared_style_file_matches_the_rulebook_byte_for_byte(string file)
    {
        string root = RepoRoot.Locate();
        string shared = Path.Combine(root, "gates", file);
        if (!File.Exists(shared)) return;
        string mine = Path.Combine(root, file);
        Assert.True(File.Exists(mine), $"{file} is missing at the repo root; copy gates/{file}");
        Assert.True(File.ReadAllText(mine).Replace("\r\n", "\n") == File.ReadAllText(shared).Replace("\r\n", "\n"), $"{file} differs from gates/{file}; copy it over, the rulebook owns it");
    }
}
