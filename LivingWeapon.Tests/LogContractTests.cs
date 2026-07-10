using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The logging facelift's enforcement gate (three checks, all source-scan-based, same
/// walk-up-from-the-test-bin-dir idiom as MetaSchemaTests.RepoMetaPath):
///
/// 1. <see cref="LogVerb"/> matches docs/LOGGING.md's committed "Event verbs" table one-for-one:
///    the doc and the enum cannot drift.
/// 2. No file outside a tiny allow-list calls a raw logger entry point (ModLogger.Log/
///    LogWarning/LogError/LogDebug/LogException, or the transitional Log.cs shim's Log.Info/
///    Log.Error); every module must route through the typed facade
///    (ModLogger.Event/Warn/Error/Debug/EventWithTrace/WarnWithTrace or a ScopedLogger from
///    ModLogger.For). This is a RATCHET: <see cref="LegacyCallers"/> lists every file that still
///    has raw calls pending the call-site conversion pass; the test asserts the scan finds
///    EXACTLY that set (not a subset), so shrinking the list without finishing the conversion,
///    or finishing the conversion without shrinking the list, both go red.
/// 3. No string literal passed to a facade call contains a double dash or an em dash (the
///    owner's "no double-dash anywhere in new text" ruling).
/// </summary>
public class LogContractTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "LOGGING.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/LOGGING.md + LivingWeapon/) not found above the test bin dir");
    }

    private static IEnumerable<string> SourceFiles(string repoRoot)
    {
        string root = Path.Combine(repoRoot, "LivingWeapon");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")));
    }

    // --- 1. LogVerb <-> docs/LOGGING.md lockstep ---

    private static readonly Regex VerbTableRowRegex = new(@"^\|\s*`([a-z][a-z-]*)`\s*\|", RegexOptions.Compiled);

    private static List<string> ParseVerbTokensFromLoggingMd(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "docs", "LOGGING.md");
        var verbs = new List<string>();
        bool inTable = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            string line = raw;
            if (!inTable)
            {
                if (line.StartsWith("| Verb |")) inTable = true;
                continue;
            }
            if (line.StartsWith("|---")) continue;
            var m = VerbTableRowRegex.Match(line);
            if (m.Success) verbs.Add(m.Groups[1].Value);
            else if (!line.StartsWith("|")) break;   // table ended
        }
        return verbs;
    }

    [Fact]
    public void LogVerb_enum_matches_the_committed_LOGGING_md_verb_table_one_for_one()
    {
        var docVerbs = ParseVerbTokensFromLoggingMd(RepoRoot());
        Assert.NotEmpty(docVerbs);
        var enumVerbs = Enum.GetValues<LogVerb>().Select(v => v.Token()).ToList();

        // No duplicates on either side (a duplicate verb row/enum member would hide a real gap).
        Assert.Equal(docVerbs.Distinct().Count(), docVerbs.Count);
        Assert.Equal(enumVerbs.Distinct().Count(), enumVerbs.Count);

        var docSet = new HashSet<string>(docVerbs);
        var enumSet = new HashSet<string>(enumVerbs);
        Assert.True(docSet.SetEquals(enumSet),
            $"docs/LOGGING.md verb table and LogVerb are out of lockstep. " +
            $"In doc but not enum: [{string.Join(", ", docSet.Except(enumSet))}]. " +
            $"In enum but not doc: [{string.Join(", ", enumSet.Except(docSet))}].");
    }

    // --- 2. Raw-logger-call ratchet ---

    /// <summary>Permanent exceptions: the facade's own plumbing. Never shrinks.</summary>
    private static readonly HashSet<string> PermanentAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModLogger.cs", "FileConsoleLogger.cs", "NullLogger.cs", "Log.cs",
    };

    /// <summary>Files still pending the call-site conversion pass (stage 2 of the logging
    /// facelift). SHRINK this set as each file's log calls migrate to the typed facade; do not
    /// add to it. EMPTY as of the stage 2 conversion: every module routes through the typed
    /// facade, and only the facade's own plumbing (the permanent allow-list above) may touch
    /// the raw entry points.</summary>
    private static readonly HashSet<string> LegacyCallers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex RawModLoggerCallRegex = new(
        @"\bModLogger\.(Log|LogWarning|LogError|LogDebug|LogException)\s*\(", RegexOptions.Compiled);

    private static readonly Regex RawLogShimCallRegex = new(
        @"(?<!Mod)\bLog\.(Info|Error)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Only_the_declared_legacy_files_still_call_a_raw_logger_entry_point()
    {
        string repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var path in SourceFiles(repoRoot))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name)) continue;
            string text = File.ReadAllText(path);
            if (RawModLoggerCallRegex.IsMatch(text) || RawLogShimCallRegex.IsMatch(text))
                offenders.Add(name);
        }
        var offenderSet = new HashSet<string>(offenders, StringComparer.OrdinalIgnoreCase);
        Assert.True(offenderSet.SetEquals(LegacyCallers),
            $"Raw-logger-call scan drifted from the declared ratchet list. " +
            $"Newly offending (add to LegacyCallers or convert instead): [{string.Join(", ", offenderSet.Except(LegacyCallers))}]. " +
            $"No longer offending (SHRINK LegacyCallers): [{string.Join(", ", LegacyCallers.Except(offenderSet))}].");
    }

    // --- 3. No double-dash / em dash inside a facade call's string literals ---

    private static readonly Regex StringLiteralRegex = new(@"\$?@?""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);

    /// <summary>Finds every call to a facade method (ModLogger.Event/Warn/Error/Debug/
    /// EventWithTrace/WarnWithTrace, or a ScopedLogger's Info/Warn/Debug; receiver must not be the
    /// transitional Log shim) and returns the string-literal contents inside each call's
    /// argument list. Balances parens/brackets/braces so interpolated-string holes
    /// (<c>$"...{foo(1,2)}..."</c>) don't truncate the scan early. Pure/testable in isolation.</summary>
    internal static List<string> FacadeCallStringLiterals(string source)
    {
        var results = new List<string>();
        // Group "recv" captures the receiver so calls on the transitional Log shim (Log.Info/
        // Log.Error) can be excluded in code: a regex lookbehind can't reject the captured
        // text itself, only what precedes the match.
        var callStart = new Regex(@"\bModLogger\.(Event|Warn|Error|Debug|EventWithTrace|WarnWithTrace)\s*\(|\b(?<recv>\w+)\.(Info|Warn|Debug)\s*\(");
        foreach (Match m in callStart.Matches(source))
        {
            if (m.Groups["recv"].Success && m.Groups["recv"].Value == "Log") continue;
            int openParen = source.IndexOf('(', m.Index);
            if (openParen < 0) continue;
            int depth = 1;
            int i = openParen + 1;
            int argsStart = i;
            for (; i < source.Length && depth > 0; i++)
            {
                char c = source[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == '"')
                {
                    // Skip over the whole string literal (handles escaped quotes) so an internal
                    // '(' or ')' inside a string can't desync the paren balance.
                    i++;
                    while (i < source.Length && source[i] != '"')
                    {
                        if (source[i] == '\\') i++;
                        i++;
                    }
                }
            }
            if (depth != 0) continue;   // unbalanced (shouldn't happen in valid C#): skip defensively
            string args = source.Substring(argsStart, i - argsStart - 1);
            foreach (Match lit in StringLiteralRegex.Matches(args))
                results.Add(lit.Value);
        }
        return results;
    }

    private static readonly char EmDash = '—';

    [Fact]
    public void FacadeCallStringLiterals_detects_a_double_dash_separator()
    {
        string snippet = "ModLogger.Event(LogVerb.Kill, \"felled a foe -- at (7,6)\");";
        var literals = FacadeCallStringLiterals(snippet);
        Assert.Contains(literals, l => l.Contains(" -- "));
    }

    [Fact]
    public void FacadeCallStringLiterals_detects_an_em_dash()
    {
        string snippet = $"ModLogger.Warn(LogVerb.Save, \"corrupt{EmDash}falling back\");";
        var literals = FacadeCallStringLiterals(snippet);
        Assert.Contains(literals, l => l.Contains(EmDash));
    }

    [Fact]
    public void FacadeCallStringLiterals_ignores_calls_on_the_transitional_Log_shim()
    {
        string snippet = "Log.Info(\"charm-lock ACTIVE -- Galewind\");";
        var literals = FacadeCallStringLiterals(snippet);
        Assert.DoesNotContain(literals, l => l.Contains(" -- "));
    }

    [Fact]
    public void FacadeCallStringLiterals_passes_a_clean_call()
    {
        string snippet = "ModLogger.Event(LogVerb.Kill, \"felled a foe at (7,6)\");";
        var literals = FacadeCallStringLiterals(snippet);
        Assert.DoesNotContain(literals, l => l.Contains(" -- ") || l.Contains(EmDash));
    }

    [Fact]
    public void No_facade_call_in_the_repo_passes_a_string_literal_with_a_double_dash_or_em_dash()
    {
        string repoRoot = RepoRoot();
        var violations = new List<string>();
        foreach (var path in SourceFiles(repoRoot))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name)) continue;   // the facade's own code, not a call site
            string text = File.ReadAllText(path);
            foreach (var lit in FacadeCallStringLiterals(text))
                if (lit.Contains(" -- ") || lit.Contains(EmDash))
                    violations.Add($"{name}: {lit}");
        }
        Assert.True(violations.Count == 0, "Facade calls with a disallowed separator:\n" + string.Join("\n", violations));
    }

    // --- 4. Subject-first lexical fence (console-eligible facade literals only) ---
    //
    // Console-eligible = the message argument of ModLogger.Event/Warn/EventWithTrace/WarnWithTrace
    // and a ScopedLogger's Info/Warn (never Error, never Debug/.Debug; those are not the
    // Info-tier match-report narrative the subject-first rule targets). This is a LEXICAL fence,
    // not full subjecthood review: it only checks the message literal's first character and
    // rejects an obvious bare "Leader:" word. Full subjecthood ("Galewind is armed..." vs some
    // other non-leader phrasing that still isn't a real subject) remains a human review rule.

    /// <summary>Dev-only instrument UI, exempt from the subject-first fence (the audit's ruling:
    /// "the console IS this instrument's user interface and none of it compiles into production;
    /// exempt from the match-report ceiling as dev scaffolding"). All three files are #if LWDEV
    /// wholesale. The double-dash scan still applies to them; only the sentence-shape rule is
    /// waived.</summary>
    private static readonly HashSet<string> FenceExemptDevFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShowSpike.cs", "FlavorSpike.cs", "HeaderSpike.cs", "AttackCardSpike.cs", "TurnOwnerSpike.cs",
        "StatusSpike.cs", "BodyDoubleSpike.cs",
    };

    private static readonly Regex LeaderPrefixRegex = new(@"^[A-Za-z][A-Za-z-]*:", RegexOptions.Compiled);

    /// <summary>Extracts the raw MESSAGE argument text for every console-eligible facade call.
    /// ModLogger.Event/Warn/EventWithTrace/WarnWithTrace take the verb first, so their message is
    /// argument index 1; a ScopedLogger's Info/Warn take only the message, index 0 (receiver must
    /// not be the transitional Log shim, which shares the "Info" method name but is not a
    /// ScopedLogger). Only literals that visibly open with a quote (plain or interpolated) are
    /// returned; a variable or method-call argument can't be lexically assessed.</summary>
    internal static List<string> ConsoleEligibleMessageLiterals(string source)
    {
        var results = new List<string>();
        CollectMessageLiterals(source, new Regex(@"\bModLogger\.(Event|Warn|EventWithTrace|WarnWithTrace)\s*\("),
            argIndex: 1, results, excludeReceiver: null);
        CollectMessageLiterals(source, new Regex(@"\b(?<recv>\w+)\.(Info|Warn)\s*\("),
            argIndex: 0, results, excludeReceiver: "Log");
        return results;
    }

    private static void CollectMessageLiterals(string source, Regex callStart, int argIndex, List<string> results, string? excludeReceiver)
    {
        foreach (Match m in callStart.Matches(source))
        {
            if (excludeReceiver != null && m.Groups["recv"].Success && m.Groups["recv"].Value == excludeReceiver) continue;
            string? args = ExtractBalancedArgs(source, m.Index);
            if (args == null) continue;
            var parts = SplitTopLevelArgs(args);
            if (argIndex >= parts.Count) continue;
            string arg = parts[argIndex].Trim();
            if (arg.StartsWith("$\"") || arg.StartsWith("\""))
                results.Add(arg);
        }
    }

    /// <summary>Balances parens (and skips over string-literal contents, so a stray '(' or ')'
    /// inside quotes can't desync the count) to return the full argument-list text of the call
    /// starting at <paramref name="matchIndex"/>. Shared shape with FacadeCallStringLiterals'
    /// scanner above; kept as an independent copy since the two serve different checks.</summary>
    private static string? ExtractBalancedArgs(string source, int matchIndex)
    {
        int openParen = source.IndexOf('(', matchIndex);
        if (openParen < 0) return null;
        int depth = 1;
        int i = openParen + 1;
        int argsStart = i;
        for (; i < source.Length && depth > 0; i++)
        {
            char c = source[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
            }
        }
        if (depth != 0) return null;
        return source.Substring(argsStart, i - argsStart - 1);
    }

    /// <summary>Splits a call's argument-list text on top-level commas only (depth-tracking
    /// parens/braces/brackets, and skipping over string-literal contents so a comma inside a
    /// message can't be mistaken for an argument separator).</summary>
    private static List<string> SplitTopLevelArgs(string args)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '(' || c == '{' || c == '[') depth++;
            else if (c == ')' || c == '}' || c == ']') depth--;
            else if (c == '"')
            {
                i++;
                while (i < args.Length && args[i] != '"')
                {
                    if (args[i] == '\\') i++;
                    i++;
                }
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(args.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(args.Substring(start));
        return parts;
    }

    /// <summary>The lexical fence itself: the message must start (after stripping the '$'/'"'
    /// literal markers) with an uppercase letter or an interpolation hole, and must not open with
    /// a bare "Word:" leader (Armed:/Locked:/Granted:/Released:/...; the audit's proposed
    /// leader-style prefixes, which read as labels rather than sentences).</summary>
    internal static bool PassesSubjectFirstFence(string literal)
    {
        string body = literal.StartsWith("$\"") ? literal.Substring(2)
            : literal.StartsWith("\"") ? literal.Substring(1)
            : literal;
        if (body.Length == 0) return false;
        char first = body[0];
        if (first == '{') return true;   // an interpolation hole; can't lexically check further
        if (!char.IsUpper(first)) return false;
        return !LeaderPrefixRegex.IsMatch(body);
    }

    [Theory]
    [InlineData("\"Galewind is armed for this battle: charms hold unbreakable\"", true)]
    [InlineData("\"{Name} claims kill number {n}\"", true)]
    [InlineData("$\"{Name} claims kill number {n}\"", true)]
    [InlineData("\"Armed: Galewind at tier three is wielded\"", false)]
    [InlineData("\"Locked: holding Charm on the enemy\"", false)]
    [InlineData("\"granted the Yoichi Bow wielder Barrage\"", false)]   // lowercase leading letter
    public void PassesSubjectFirstFence_lexical_cases(string literal, bool expected)
        => Assert.Equal(expected, PassesSubjectFirstFence(literal));

    [Fact]
    public void No_console_eligible_facade_call_in_the_repo_opens_with_a_bare_leader_word()
    {
        string repoRoot = RepoRoot();
        var violations = new List<string>();
        foreach (var path in SourceFiles(repoRoot))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name) || FenceExemptDevFiles.Contains(name)) continue;
            string text = File.ReadAllText(path);
            foreach (var lit in ConsoleEligibleMessageLiterals(text))
                if (!PassesSubjectFirstFence(lit))
                    violations.Add($"{name}: {lit}");
        }
        Assert.True(violations.Count == 0, "Console-eligible facade calls failing the subject-first lexical fence:\n" + string.Join("\n", violations));
    }
}
