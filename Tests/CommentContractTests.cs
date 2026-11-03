using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// A comment says what the code does or why, never which ticket or review asked for it. Reads
/// only the comment part of each line in C#, Python and PowerShell, and fails on a ticket id, a
/// review round, a reviewer or a verifier, and on a file or type named after a round.
/// </summary>
public class CommentContractTests
{
    private static readonly Regex TicketRegex = new(@"\b(R\d+-\d+[a-z]?|[A-Z]{2}-\d+)\b", RegexOptions.Compiled);
    private static readonly Regex ReviewRegex = new(@"\b(round\s?\d+|reviewer|verifier)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RoundNameRegex = new(@"(?<![a-z])round\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TypeNameRegex = new(@"\b(class|record|struct|interface|enum)\s+(\w+)", RegexOptions.Compiled);

    /// <summary>This file names the patterns it bans, so it cannot scan itself.</summary>
    private static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase) { "CommentContractTests.cs" };

    private static IEnumerable<string> ScannedFiles()
    {
        string root = RepoRoot.Locate();
        var rules = Rules.Load();
        foreach (var folder in new[] { rules.ProdDir, rules.TestsDir })
            if (Directory.Exists(folder))
                foreach (var f in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
                    if (!f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", ""))) yield return f;
        string tools = Path.Combine(root, "tools");
        if (Directory.Exists(tools))
            foreach (var f in Directory.EnumerateFiles(tools, "*.*", SearchOption.TopDirectoryOnly))
                if (f.EndsWith(".py") || f.EndsWith(".ps1")) yield return f;
        foreach (var f in Directory.EnumerateFiles(root, "*.ps1", SearchOption.TopDirectoryOnly)) yield return f;
        foreach (var folder in rules.PythonProd)
        {
            string dir = Path.Combine(root, folder.TrimEnd('/'));
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories))
                if (!f.Contains(Path.Combine("__pycache__", ""))) yield return f;
        }
    }

    /// <summary>The comment part of one line. A block comment carries its state to the next line.</summary>
    internal static string CommentText(string path, string line, ref bool inBlock)
    {
        bool cs = path.EndsWith(".cs");
        string blockOpen = cs ? "/*" : "<#", blockClose = cs ? "*/" : "#>";
        var text = new System.Text.StringBuilder();
        int i = 0;
        while (i < line.Length)
        {
            if (inBlock)
            {
                int close = line.IndexOf(blockClose, i, StringComparison.Ordinal);
                if (close < 0) { text.Append(line, i, line.Length - i); return text.ToString(); }
                text.Append(line, i, close - i).Append(' ');
                i = close + blockClose.Length;
                inBlock = false;
                continue;
            }
            char c = line[i];
            if (c == '"') { i = LogScan.SkipStringLiteral(line, i); continue; }
            if (cs && c == '\'' && i + 2 < line.Length && (line[i + 2] == '\'' || (line[i + 1] == '\\' && i + 3 < line.Length && line[i + 3] == '\'')))
            {
                i += line[i + 1] == '\\' ? 4 : 3;
                continue;
            }
            if (cs && c == '/' && i + 1 < line.Length && line[i + 1] == '/') { text.Append(line, i + 2, line.Length - i - 2); return text.ToString(); }
            if (!cs && c == '#' && !(path.EndsWith(".ps1") && i > 0 && line[i - 1] == '<')) { text.Append(line, i + 1, line.Length - i - 1); return text.ToString(); }
            if (string.CompareOrdinal(line, i, blockOpen, 0, 2) == 0 && (cs || path.EndsWith(".ps1"))) { inBlock = true; i += 2; continue; }
            i++;
        }
        return text.ToString();
    }

    internal static string? Offence(string commentText)
    {
        var m = TicketRegex.Match(commentText);
        if (!m.Success) m = ReviewRegex.Match(commentText);
        return m.Success ? m.Value : null;
    }

    [Theory]
    [InlineData("a.cs", "// a precondition only guards the phases it can still protect (R9-1)", "R9-1")]
    [InlineData("a.cs", "/// ZZ-13: while holding, log the first re-ask", "ZZ-13")]
    [InlineData("a.cs", "int x = 1; // per R9-1", "R9-1")]
    [InlineData("a.cs", "var s = \"R9-1 in a string\"; // fine", null)]
    [InlineData("a.cs", "/* round 3 */", "round 3")]
    [InlineData("a.cs", "char q = '\"'; // the reviewer asked", "reviewer")]
    [InlineData("a.py", "x = \"ZZ-4\"  # round 12", "round 12")]
    [InlineData("a.ps1", "# per round 12", "round 12")]
    [InlineData("a.cs", "// the engine executes an attack from four tiles away if asked, seen live 2026-10-26", null)]
    [InlineData("a.cs", "// one record per owned turn", null)]
    public void Offence_names_the_banned_token_in_the_comment_portion_or_nothing(string path, string line, string? expected)
    {
        bool inBlock = false;
        Assert.Equal(expected, Offence(CommentText(path, line, ref inBlock)));
    }

    [Fact]
    public void Block_comments_carry_across_lines_in_both_languages()
    {
        bool inBlock = false;
        Assert.Null(Offence(CommentText("a.cs", "int a = 1; /* opens", ref inBlock)));
        Assert.True(inBlock);
        Assert.Equal("R9-2", Offence(CommentText("a.cs", "   see R9-2 */ int b = 2;", ref inBlock)));
        Assert.False(inBlock);
        Assert.Null(Offence(CommentText("a.ps1", "<#", ref inBlock)));
        Assert.True(inBlock);
        Assert.Equal("reviewer", Offence(CommentText("a.ps1", "  the reviewer wanted this", ref inBlock)));
        Assert.Null(Offence(CommentText("a.ps1", "#>", ref inBlock)));
        Assert.False(inBlock);
    }

    [Fact]
    public void No_comment_file_name_or_type_name_carries_a_ticket_or_review_reference()
    {
        var violations = new List<string>();
        int scanned = 0;
        foreach (var path in ScannedFiles().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path);
            if (Exempt.Contains(name)) continue;
            scanned++;
            if (RoundNameRegex.IsMatch(Path.GetFileNameWithoutExtension(path))) violations.Add($"{name}: file name");
            var lines = File.ReadAllLines(path);
            bool inBlock = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (path.EndsWith(".cs"))
                    foreach (Match tm in TypeNameRegex.Matches(lines[i]))
                        if (RoundNameRegex.IsMatch(tm.Groups[2].Value)) violations.Add($"{name}:{i + 1}: type name {tm.Groups[2].Value}");
                var hit = Offence(CommentText(path, lines[i], ref inBlock));
                if (hit != null) violations.Add($"{name}:{i + 1}: {hit}");
            }
        }
        Assert.True(scanned > 0, "no files scanned; the walk is broken");
        Assert.True(violations.Count == 0, "Say what the code does or why, not which ticket or review asked for it:\n" + string.Join("\n", violations));
    }
}
