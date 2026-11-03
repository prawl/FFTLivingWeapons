using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ModGates.Logging;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// The logging rules. The verb enum and the doc's verb table match, no file writes to the
/// console itself or calls a retired logger entry point, no log text carries a double dash or an
/// em dash, and every message a player may see opens subject first.
/// </summary>
public class LogContractTests
{
    private static readonly Regex VerbTableRow = new(@"^\|\s*`([a-z][a-z-]*)`\s*\|", RegexOptions.Compiled);
    private static readonly Regex VerbLessCall = new(@"\bModLogger\.Info\s*\(|\bModLogger\.(Warn|Error|Event|Debug)\s*\(\s*\$?@?""", RegexOptions.Compiled);
    private static readonly Regex ConsoleWrite = new(@"\bConsole\.(WriteLine|Write|Error)\b", RegexOptions.Compiled);
    private static readonly Regex VerbLessMember = new(@"\b(Log|LogWarning|LogError|LogDebug|LogException)\s*\(\s*string\b", RegexOptions.Compiled);
    private static readonly Regex TypedMember = new(@"\b(Log|LogWarning|LogError|LogDebug)\s*\(\s*LogVerb\b", RegexOptions.Compiled);
    private static readonly Regex RetiredCall = new(@"\bModLogger\.(Log|LogWarning|LogError|LogDebug|LogException)\s*\(|(?<!Mod)\bLog\.(Info|Error|Init)\s*\(", RegexOptions.Compiled);

    private static List<string> ParseVerbTokens()
    {
        string path = Path.Combine(RepoRoot.Locate(), Rules.Load().LoggingDoc);
        var verbs = new List<string>();
        bool inTable = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            if (!inTable) { if (raw.StartsWith("| Verb |")) inTable = true; continue; }
            if (raw.StartsWith("|-")) continue;
            var m = VerbTableRow.Match(raw);
            if (m.Success) verbs.Add(m.Groups[1].Value);
            else if (!raw.StartsWith("|")) break;
        }
        return verbs;
    }

    [Fact]
    public void LogVerb_enum_matches_the_committed_LOGGING_md_verb_table_one_for_one()
    {
        var docVerbs = ParseVerbTokens();
        Assert.NotEmpty(docVerbs);
        var enumVerbs = Enum.GetValues<LogVerb>().Select(v => v.Token()).ToList();
        Assert.Equal(docVerbs.Distinct().Count(), docVerbs.Count);
        Assert.Equal(enumVerbs.Distinct().Count(), enumVerbs.Count);
        var docSet = new HashSet<string>(docVerbs);
        var enumSet = new HashSet<string>(enumVerbs);
        Assert.True(docSet.SetEquals(enumSet),
            $"{Rules.Load().LoggingDoc} verb table and LogVerb are out of lockstep. In doc only: [{string.Join(", ", docSet.Except(enumSet))}]. In enum only: [{string.Join(", ", enumSet.Except(docSet))}].");
    }

    [Fact]
    public void Every_facade_member_takes_a_verb_first_and_no_call_site_passes_a_bare_message()
    {
        foreach (var m in typeof(ModLogger).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name is "Event" or "Warn" or "Error" or "Debug" or "EventWithTrace" or "WarnWithTrace"))
            Assert.Equal(typeof(LogVerb), m.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(typeof(ModLogger).GetMethods(), m => m.Name == "Info");
        var offenders = Offenders(VerbLessCall, exempt: null);
        Assert.True(offenders.Count == 0, "Files logging without a verb (use ModLogger.Event/Warn/Error/Debug with a LogVerb first):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Console_writes_live_only_inside_the_facade_or_the_shrinking_allowlist()
    {
        var exempt = new HashSet<string>(LogScan.FacadeFiles.Concat(Rules.Load().ConsoleAllowlist), StringComparer.OrdinalIgnoreCase);
        var offenders = Offenders(ConsoleWrite, exempt);
        Assert.True(offenders.Count == 0, "Files writing to the console outside the facade plumbing (route through ModLogger; never add to the allowlist):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_shrinking_allowlist_does_not_rot()
    {
        var stale = new List<string>();
        var byName = LogScan.ProductionFiles().ToLookup(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Rules.Load().ConsoleAllowlist)
        {
            var matches = byName[name].ToList();
            if (matches.Count == 0) { stale.Add($"{name}: no such production file"); continue; }
            if (!matches.Any(p => ConsoleWrite.IsMatch(File.ReadAllText(p))))
                stale.Add($"{name}: contains no raw Console write anymore; remove it from consoleAllowlist");
        }
        Assert.True(stale.Count == 0, "Stale allowlist entries:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void No_verb_less_logger_member_is_declared_anywhere_in_the_runtime()
    {
        var offenders = new List<string>();
        bool sawTypedLane = false;
        foreach (var path in LogScan.ProductionFiles().Concat(LogScan.LoggerFiles()))
        {
            string text = File.ReadAllText(path);
            if (VerbLessMember.IsMatch(text)) offenders.Add(Path.GetFileName(path));
            if (TypedMember.IsMatch(text)) sawTypedLane = true;
        }
        Assert.True(sawTypedLane, "The scan never saw the typed lane's LogVerb-first members, so it cannot be trusted to catch a verb-less one.");
        Assert.True(offenders.Count == 0, "Files declaring a bare-string logger member (every member takes a LogVerb first):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_file_calls_a_retired_logger_entry_point()
    {
        var offenders = Offenders(RetiredCall, exempt: null);
        Assert.True(offenders.Count == 0, "Files calling a retired logger entry point (route through ModLogger.Event/Warn/Error/Debug):\n" + string.Join("\n", offenders));
    }

    private static List<string> Offenders(Regex pattern, HashSet<string>? exempt)
    {
        var offenders = new List<string>();
        foreach (var path in LogScan.ProductionFiles())
        {
            string name = Path.GetFileName(path);
            if (exempt != null && exempt.Contains(name)) continue;
            if (pattern.IsMatch(File.ReadAllText(path))) offenders.Add(name);
        }
        return offenders;
    }

    [Fact]
    public void FacadeCallStringLiterals_detects_a_double_dash_separator()
    {
        Assert.Contains(LogScan.FacadeCallStringLiterals("ModLogger.Event(LogVerb.Gate, \"refused -- out of range\");"), l => l.Contains(" -- "));
    }

    [Fact]
    public void FacadeCallStringLiterals_detects_an_em_dash()
    {
        Assert.Contains(LogScan.FacadeCallStringLiterals($"ModLogger.Warn(LogVerb.Gate, \"corrupt{LogScan.EmDash}falling back\");"), l => l.Contains(LogScan.EmDash));
    }

    [Fact]
    public void FacadeCallStringLiterals_passes_a_clean_call()
    {
        Assert.DoesNotContain(LogScan.FacadeCallStringLiterals("ModLogger.Event(LogVerb.Gate, @\"C:\\logs\\\");"), l => l.Contains(" -- ") || l.Contains(LogScan.EmDash));
        Assert.DoesNotContain(LogScan.FacadeCallStringLiterals("ModLogger.Event(LogVerb.Gate, \"A treasure was claimed at (7,6).\");"), l => l.Contains(" -- ") || l.Contains(LogScan.EmDash));
    }

    [Fact]
    public void FacadeCallStringLiterals_ignores_calls_on_the_transitional_Log_shim()
    {
        Assert.DoesNotContain(LogScan.FacadeCallStringLiterals("Log.Info(\"charm-lock ACTIVE -- Galewind\");"), l => l.Contains(" -- "));
    }

    [Fact]
    public void FacadeCallStringLiterals_captures_a_verbatim_path_literal_ending_in_a_backslash()
    {
        var literals = LogScan.FacadeCallStringLiterals("ModLogger.Event(LogVerb.Gate, @\"C:\\Users\\ptyRa\\\");");
        Assert.Contains(literals, l => l == "@\"C:\\Users\\ptyRa\\\"");
    }

    [Fact]
    public void ConsoleEligibleMessageLiterals_is_not_desynced_by_a_verbatim_path_argument_ending_in_a_backslash()
    {
        var literals = LogScan.ConsoleEligibleMessageLiterals("ModLogger.EventWithTrace(LogVerb.Gate, \"Loaded the save file\", @\"path=C:\\Users\\ptyRa\\\");");
        Assert.Contains(literals, l => l == "\"Loaded the save file\"");
    }

    [Fact]
    public void No_facade_call_in_the_repo_passes_a_string_literal_with_a_double_dash_or_em_dash()
    {
        var violations = new List<string>();
        int calls = 0;
        foreach (var path in LogScan.ProductionFiles())
        {
            string name = Path.GetFileName(path);
            if (LogScan.FacadeFiles.Contains(name)) continue;
            string text = File.ReadAllText(path);
            calls += LogScan.FacadeCallCount(text);
            foreach (var lit in LogScan.FacadeCallStringLiterals(text))
                if (lit.Contains(" -- ") || lit.Contains(LogScan.EmDash)) violations.Add($"{name}: {lit}");
        }
        Assert.True(calls > 0, "The scan saw no facade call at all, so it is reading the wrong files.");
        Assert.True(violations.Count == 0, "Facade calls with a disallowed separator:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void No_trace_detail_in_the_repo_doubles_the_trace_bracket()
    {
        Assert.Contains(LogScan.TraceDetailLiterals("ModLogger.EventWithTrace(LogVerb.Gate, \"Hooked.\", $\"[trace] 0x{a:X}\");"), l => l.Contains("[trace]"));
        var violations = new List<string>();
        foreach (var path in LogScan.ProductionFiles())
            foreach (var lit in LogScan.TraceDetailLiterals(File.ReadAllText(path)))
                if (lit.TrimStart('$', '@', '"').StartsWith("[trace]")) violations.Add($"{Path.GetFileName(path)}: {lit}");
        Assert.True(violations.Count == 0, "Trace details opening with [trace] (the verb already renders it):\n" + string.Join("\n", violations));
    }

    [Theory]
    [InlineData("\"The gate refused an attack because the target is out of range.\"", true)]
    [InlineData("$\"{count} plans were rejected.\"", true)]
    [InlineData("\"gate: refused\"", false)]
    [InlineData("\"Refused: out of range\"", false)]
    [InlineData("\"waiting for a plan\"", false)]
    [InlineData("\"Galewind is armed for this battle: charms hold unbreakable\"", true)]
    [InlineData("\"{Name} claims kill number {n}\"", true)]
    [InlineData("$\"{Name} claims kill number {n}\"", true)]
    [InlineData("\"Armed: Galewind at tier three is wielded\"", false)]
    [InlineData("\"Locked: holding Charm on the enemy\"", false)]
    [InlineData("\"granted the Yoichi Bow wielder Barrage\"", false)]
    [InlineData("\"Map 74 is armed: 4 treasure tiles held lit.\"", true)]
    [InlineData("$\"{count} tiles are claimed.\"", true)]
    [InlineData("\"treasure: map 74 armed\"", false)]
    [InlineData("\"Armed: map 74\"", false)]
    [InlineData("\"waiting to arm\"", false)]
    [InlineData("\"The theme corvid is now applied to Knight: 2 files swapped.\"", true)]
    [InlineData("$\"{count} selections were saved.\"", true)]
    [InlineData("\"theme: corvid applied\"", false)]
    [InlineData("\"Applied: corvid\"", false)]
    [InlineData("\"applied corvid to knight\"", false)]
    public void PassesSubjectFirstFence_lexical_cases(string literal, bool expected) => Assert.Equal(expected, LogScan.PassesSubjectFirstFence(literal));

    [Fact]
    public void No_console_eligible_facade_call_in_the_repo_opens_with_a_bare_leader_word()
    {
        var exempt = new HashSet<string>(LogScan.FacadeFiles.Concat(Rules.Load().FenceExemptFiles), StringComparer.OrdinalIgnoreCase);
        var violations = new List<string>();
        int calls = 0;
        foreach (var path in LogScan.ProductionFiles())
        {
            string name = Path.GetFileName(path);
            if (exempt.Contains(name)) continue;
            string text = File.ReadAllText(path);
            calls += LogScan.FacadeCallCount(text);
            foreach (var lit in LogScan.ConsoleEligibleMessageLiterals(text))
                if (!LogScan.PassesSubjectFirstFence(lit)) violations.Add($"{name}: {lit}");
        }
        Assert.True(calls > 0, "The scan saw no facade call at all, so it is reading the wrong files.");
        Assert.True(violations.Count == 0, "Console-eligible facade calls failing the subject-first fence:\n" + string.Join("\n", violations));
    }
}
