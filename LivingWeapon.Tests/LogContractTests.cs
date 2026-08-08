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
/// 2. The verb-less legacy lane STAYS DELETED (LW-155). The migration this section once
///    ratcheted (LegacyCallers, a per-file allow-list shrunk to empty) finished, and the lane
///    itself (the five bare-string ModLogger statics, ILogger's bare-string members, their
///    FileConsoleLogger/NullLogger implementations) was then removed outright. What remains is
///    a TRIPWIRE, not a ceremony: it goes red if anyone re-declares a verb-less logger member
///    or re-adds a raw call site, so the lane cannot quietly grow back.
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

    // --- 2. Verb-less-lane tripwire (LW-155: the lane is deleted; keep it that way) ---

    /// <summary>The facade's own plumbing: exempt from the string-literal checks (sections 3/4)
    /// because its files are not call sites. The tripwires in THIS section deliberately scan
    /// these files too -- the verb-less lane must not return even inside the facade. Never
    /// shrinks.</summary>
    private static readonly HashSet<string> PermanentAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModLogger.cs", "FileConsoleLogger.cs", "NullLogger.cs",
    };

    /// <summary>A verb-less logger member: one of the five legacy names whose first parameter is
    /// a bare string. The typed lane's first parameter is always a LogVerb, so this shape only
    /// exists if someone re-declares the deleted lane (or a lookalike of it).</summary>
    private static readonly Regex VerbLessMemberRegex = new(
        @"\b(Log|LogWarning|LogError|LogDebug|LogException)\s*\(\s*string\b", RegexOptions.Compiled);

    /// <summary>The typed lane's LogVerb-first shape: the tripwire's non-vacuity proof. If this
    /// stops matching anywhere, the scan is reading the wrong files (or the member naming moved)
    /// and the verb-less tripwire above would pass while proving nothing.</summary>
    private static readonly Regex TypedMemberRegex = new(
        @"\b(Log|LogWarning|LogError|LogDebug)\s*\(\s*LogVerb\b", RegexOptions.Compiled);

    private static readonly Regex RawModLoggerCallRegex = new(
        @"\bModLogger\.(Log|LogWarning|LogError|LogDebug|LogException)\s*\(", RegexOptions.Compiled);

    private static readonly Regex RawLogShimCallRegex = new(
        @"(?<!Mod)\bLog\.(Info|Error)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void No_verb_less_logger_member_is_declared_anywhere_in_the_runtime()
    {
        string repoRoot = RepoRoot();
        var offenders = new List<string>();
        bool sawTypedLane = false;
        foreach (var path in SourceFiles(repoRoot))
        {
            string text = File.ReadAllText(path);
            if (VerbLessMemberRegex.IsMatch(text))
                offenders.Add(Path.GetFileName(path));
            if (TypedMemberRegex.IsMatch(text))
                sawTypedLane = true;
        }
        Assert.True(sawTypedLane,
            "Non-vacuity proof failed: the scan never saw the typed lane's LogVerb-first members, " +
            "so it cannot be trusted to catch a reintroduced verb-less one.");
        Assert.True(offenders.Count == 0,
            "The verb-less logger lane was deleted in LW-155 and must not be reintroduced. " +
            $"Files declaring a bare-string logger member: [{string.Join(", ", offenders)}]. " +
            "Route the new need through the typed facade (a LogVerb-first member) instead.");
    }

    [Fact]
    public void No_file_calls_a_deleted_raw_logger_entry_point()
    {
        // The compiler already rejects a raw ModLogger.Log(...) call (the member is gone); this
        // scan stays because it fails with a message naming the deletion instead of a bare
        // CS0117, and because it also catches a re-created transitional Log shim (a class named
        // Log with Info/Error members would compile just fine).
        string repoRoot = RepoRoot();
        var offenders = new List<string>();
        foreach (var path in SourceFiles(repoRoot))
        {
            string text = File.ReadAllText(path);
            if (RawModLoggerCallRegex.IsMatch(text) || RawLogShimCallRegex.IsMatch(text))
                offenders.Add(Path.GetFileName(path));
        }
        Assert.True(offenders.Count == 0,
            "The raw (verb-less) logger entry points were deleted in LW-155; nothing may call " +
            $"one. Offending files: [{string.Join(", ", offenders)}].");
    }

    // --- 3. No double-dash / em dash inside a facade call's string literals ---

    /// <summary>Skips a single string literal starting at <paramref name="quoteIndex"/> (the
    /// index of its OPENING '"' in <paramref name="text"/>) and returns the index just past its
    /// closing quote. Verbatim-aware: a REGULAR (or interpolated-but-not-verbatim, i.e. plain
    /// <c>"..."</c> or <c>$"..."</c>) string treats backslash as an escape, so <c>\"</c> does not
    /// end it; a VERBATIM string (<c>@"..."</c>, <c>$@"..."</c>, or <c>@$"..."</c> -- detected by
    /// walking backward from <paramref name="quoteIndex"/> over any run of '@'/'$' prefix chars
    /// and checking for an '@' among them) treats backslash as an ordinary LITERAL character that
    /// never escapes anything; a lone '"' always ends it, and the only escape mechanism is
    /// DOUBLING the quote (<c>""</c> -&gt; a literal embedded '"'). Getting this wrong is exactly
    /// the LW-147 bug: the old code applied backslash-escape rules unconditionally, so a verbatim
    /// path ending in a backslash right before the closing quote (e.g. <c>@"C:\Users\"</c>) had
    /// its real closing quote swallowed as if it were an escaped character, desyncing every
    /// paren/quote count for the rest of the scan.</summary>
    private static int SkipStringLiteral(string text, int quoteIndex)
    {
        bool verbatim = false;
        int p = quoteIndex - 1;
        while (p >= 0 && (text[p] == '@' || text[p] == '$'))
        {
            if (text[p] == '@') verbatim = true;
            p--;
        }

        int i = quoteIndex + 1;
        if (verbatim)
        {
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }   // "" -> literal quote
                    return i + 1;
                }
                i++;
            }
            return i;   // unterminated (shouldn't happen in valid C#)
        }
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }   // backslash escapes the next char
            if (text[i] == '"') return i + 1;
            i++;
        }
        return i;   // unterminated (shouldn't happen in valid C#)
    }

    /// <summary>Scans <paramref name="text"/> for every string literal (any of the four quote
    /// prefixes: plain, <c>$</c>, <c>@</c>, <c>$@</c>/<c>@$</c>) and returns each one's full
    /// source text (prefix chars + quotes included), verbatim-aware via <see
    /// cref="SkipStringLiteral"/>. Replaces a plain regex extraction, which -- like the manual
    /// paren-balance loops below -- treated every string as backslash-escaped and so mis-scanned
    /// (silently dropping, not crashing on) a verbatim literal ending in a backslash.</summary>
    private static List<string> FindStringLiterals(string text)
    {
        var found = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '"') continue;
            int start = i;
            while (start > 0 && (text[start - 1] == '@' || text[start - 1] == '$')) start--;
            int end = SkipStringLiteral(text, i);
            found.Add(text.Substring(start, end - start));
            i = end - 1;   // the for loop's i++ resumes right after this literal
        }
        return found;
    }

    /// <summary>Finds every call to a facade method (ModLogger.Event/Warn/Error/Debug/
    /// EventWithTrace/WarnWithTrace, or a ScopedLogger's Info/Warn/Debug; receiver must not be the
    /// transitional Log shim) and returns the string-literal contents inside each call's
    /// argument list. Balances parens/brackets/braces so interpolated-string holes
    /// (<c>$"...{foo(1,2)}..."</c>) don't truncate the scan early. Pure/testable in isolation.</summary>
    internal static List<string> FacadeCallStringLiterals(string source)
    {
        var results = new List<string>();
        // Group "recv" captures the receiver so a call on a class literally named "Log" (the
        // retired transitional shim's name; Log.cs is gone, but the name guard stays as a
        // regression fence) can be excluded in code: a regex lookbehind can't reject the
        // captured text itself, only what precedes the match.
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
                    // Skip over the whole string literal (verbatim-aware) so an internal '(' or
                    // ')' -- or a verbatim path's trailing backslash -- can't desync the paren
                    // balance.
                    i = SkipStringLiteral(source, i) - 1;   // -1: the for loop's own i++ lands right after
                }
            }
            if (depth != 0) continue;   // unbalanced (shouldn't happen in valid C#): skip defensively
            string args = source.Substring(argsStart, i - argsStart - 1);
            foreach (var lit in FindStringLiterals(args))
                results.Add(lit);
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

    // ---- LW-147: verbatim-string semantics. Both the inline balance loop above (inside
    // FacadeCallStringLiterals) and ExtractBalancedArgs below treated backslash as a universal
    // escape, which is wrong for a verbatim (@"...") or verbatim-interpolated ($@"..."/@$"...")
    // string literal: there, backslash is a literal character and the only escape is a DOUBLED
    // quote. A verbatim path ending in a backslash right before the closing quote (e.g.
    // @"C:\Users\") had its real closing quote swallowed as "escaped", desyncing the scan for
    // the rest of the call (and, for FacadeCallStringLiterals, the rest of the source text). ----

    [Fact]
    public void FacadeCallStringLiterals_captures_a_verbatim_path_literal_ending_in_a_backslash()
    {
        string snippet = "ModLogger.Event(LogVerb.Save, @\"C:\\Users\\ptyRa\\\");";
        // The literal text the scanner should see, unescaped: ModLogger.Event(LogVerb.Save, @"C:\Users\ptyRa\");
        var literals = FacadeCallStringLiterals(snippet);
        Assert.Contains(literals, l => l == "@\"C:\\Users\\ptyRa\\\"");
    }

    [Fact]
    public void ConsoleEligibleMessageLiterals_is_not_desynced_by_a_verbatim_path_argument_ending_in_a_backslash()
    {
        // The message (arg index 1) is a perfectly normal literal; the TRAILING traceDetail
        // argument (arg index 2, not itself checked by this scanner) is a verbatim path ending
        // in a backslash. The old ExtractBalancedArgs desynced scanning THAT argument, so depth
        // never rebalanced and the whole call -- including the innocent message -- vanished from
        // the results.
        string snippet = "ModLogger.EventWithTrace(LogVerb.Save, \"Loaded the save file\", @\"path=C:\\Users\\ptyRa\\\");";
        var literals = ConsoleEligibleMessageLiterals(snippet);
        Assert.Contains(literals, l => l == "\"Loaded the save file\"");
    }

    [Fact]
    public void No_facade_call_in_the_repo_passes_a_string_literal_with_a_double_dash_or_em_dash()
    {
        string repoRoot = RepoRoot();
        var violations = new List<string>();
        var allLiterals = new List<string>();
        foreach (var path in SourceFiles(repoRoot))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name)) continue;   // the facade's own code, not a call site
            string text = File.ReadAllText(path);
            foreach (var lit in FacadeCallStringLiterals(text))
            {
                allLiterals.Add(lit);
                if (lit.Contains(" -- ") || lit.Contains(EmDash))
                    violations.Add($"{name}: {lit}");
            }
        }
        // A scan that finds zero facade literals means the scanner desynced, not that the repo is clean.
        Assert.NotEmpty(allLiterals);
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
    /// exempt from the match-report ceiling as dev scaffolding"). The file is #if LWDEV wholesale.
    /// The double-dash scan still applies to it; only the sentence-shape rule is waived. (The F6
    /// dev spikes that used to sit here were removed in LW-67; TurnOwnerSpike, StatusSpike, and
    /// BodyDoubleSpike remain.)</summary>
    private static readonly HashSet<string> FenceExemptDevFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TurnOwnerSpike.cs", "StatusSpike.cs", "BodyDoubleSpike.cs", "ProvokeSpike.cs", "NumeralSpike.cs",
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

    /// <summary>Balances parens (and skips over string-literal contents, verbatim-aware via
    /// <see cref="SkipStringLiteral"/>, so a stray '(' or ')' -- or a verbatim path's trailing
    /// backslash -- inside quotes can't desync the count) to return the full argument-list text
    /// of the call starting at <paramref name="matchIndex"/>. Shared shape with
    /// FacadeCallStringLiterals' scanner above; kept as an independent copy since the two serve
    /// different checks.</summary>
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
                i = SkipStringLiteral(source, i) - 1;   // -1: the for loop's own i++ lands right after
            }
        }
        if (depth != 0) return null;
        return source.Substring(argsStart, i - argsStart - 1);
    }

    /// <summary>Splits a call's argument-list text on top-level commas only (depth-tracking
    /// parens/braces/brackets, and skipping over string-literal contents, verbatim-aware via
    /// <see cref="SkipStringLiteral"/>, so a comma -- or a verbatim path's trailing backslash --
    /// inside a message can't be mistaken for an argument separator or a false string end).
    /// Shares the same bug class ExtractBalancedArgs and FacadeCallStringLiterals had: a
    /// verbatim argument earlier in the list that ends in a backslash right before its closing
    /// quote would otherwise swallow the FOLLOWING top-level comma as "inside the string",
    /// silently merging two arguments into one.</summary>
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
                i = SkipStringLiteral(args, i) - 1;   // -1: the for loop's own i++ lands right after
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
        var allLiterals = new List<string>();
        foreach (var path in SourceFiles(repoRoot))
        {
            string name = Path.GetFileName(path);
            if (PermanentAllowList.Contains(name) || FenceExemptDevFiles.Contains(name)) continue;
            string text = File.ReadAllText(path);
            foreach (var lit in ConsoleEligibleMessageLiterals(text))
            {
                allLiterals.Add(lit);
                if (!PassesSubjectFirstFence(lit))
                    violations.Add($"{name}: {lit}");
            }
        }
        // A scan that finds zero facade literals means the scanner desynced, not that the repo is clean.
        Assert.NotEmpty(allLiterals);
        Assert.True(violations.Count == 0, "Console-eligible facade calls failing the subject-first lexical fence:\n" + string.Join("\n", violations));
    }
}
