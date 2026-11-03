using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModGates.Tests;

/// <summary>Reads C# source for logger calls and the string literals inside them.</summary>
internal static class LogScan
{
    /// <summary>The facade's own files. They may touch a raw sink and are not call sites.</summary>
    public static readonly HashSet<string> FacadeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ModLogger.cs", "FileConsoleLogger.cs", "NullLogger.cs",
    };

    public const char EmDash = '—';

    private static readonly Regex FacadeCall = new(@"\bModLogger\.(Event|Warn|Error|Debug|EventWithTrace|WarnWithTrace)\s*\(|\b(?<recv>\w+)\.(Info|Warn|Debug)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ConsoleEligibleCall = new(@"\bModLogger\.(Event|Warn|EventWithTrace|WarnWithTrace)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ScopedCall = new(@"\b(?<recv>\w+)\.(Info|Warn)\s*\(", RegexOptions.Compiled);
    private static readonly Regex TraceCall = new(@"\bModLogger\.(EventWithTrace|WarnWithTrace)\s*\(", RegexOptions.Compiled);
    private static readonly Regex LeaderPrefix = new(@"^[A-Za-z][A-Za-z-]*:", RegexOptions.Compiled);

    /// <summary>Every production source file, build output skipped.</summary>
    public static IEnumerable<string> ProductionFiles()
        => Directory.EnumerateFiles(Rules.Load().ProdDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")));

    /// <summary>The shared logger's files when they sit beside the mod, so a tripwire sees the typed lane.</summary>
    public static IEnumerable<string> LoggerFiles()
    {
        string folder = Path.Combine(RepoRoot.Locate(), "gates", "Logging");
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.cs") : Enumerable.Empty<string>();
    }

    /// <summary>Skips one string literal from its opening quote. A verbatim string doubles its quotes instead of escaping.</summary>
    public static int SkipStringLiteral(string text, int quoteIndex)
    {
        bool verbatim = false;
        int p = quoteIndex - 1;
        while (p >= 0 && (text[p] == '@' || text[p] == '$')) { if (text[p] == '@') verbatim = true; p--; }
        int i = quoteIndex + 1;
        if (verbatim)
        {
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
                i++;
            }
            return i;
        }
        while (i < text.Length)
        {
            if (text[i] == '\\') { i += 2; continue; }
            if (text[i] == '"') return i + 1;
            i++;
        }
        return i;
    }

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
            i = end - 1;
        }
        return found;
    }

    private static string? ExtractBalancedArgs(string source, int matchIndex)
    {
        int openParen = source.IndexOf('(', matchIndex);
        if (openParen < 0) return null;
        int depth = 1, i = openParen + 1, argsStart = i;
        for (; i < source.Length && depth > 0; i++)
        {
            char c = source[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '"') i = SkipStringLiteral(source, i) - 1;
        }
        return depth == 0 ? source.Substring(argsStart, i - argsStart - 1) : null;
    }

    private static List<string> SplitTopLevelArgs(string args)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c == '(' || c == '{' || c == '[') depth++;
            else if (c == ')' || c == '}' || c == ']') depth--;
            else if (c == '"') i = SkipStringLiteral(args, i) - 1;
            else if (c == ',' && depth == 0) { parts.Add(args.Substring(start, i - start)); start = i + 1; }
        }
        parts.Add(args.Substring(start));
        return parts;
    }

    /// <summary>How many facade calls a file holds. Zero across a whole repo means the scanner is reading the wrong files.</summary>
    public static int FacadeCallCount(string source)
        => FacadeCall.Matches(source).Count(m => !(m.Groups["recv"].Success && m.Groups["recv"].Value == "Log"));

    /// <summary>Every string literal inside a facade call. A call on a class named Log is the retired shim and is skipped.</summary>
    public static List<string> FacadeCallStringLiterals(string source)
    {
        var results = new List<string>();
        foreach (Match m in FacadeCall.Matches(source))
        {
            if (m.Groups["recv"].Success && m.Groups["recv"].Value == "Log") continue;
            string? args = ExtractBalancedArgs(source, m.Index);
            if (args != null) results.AddRange(FindStringLiterals(args));
        }
        return results;
    }

    private static readonly HashSet<string> NotScopedReceivers = new() { "ModLogger", "Log" };

    private static void CollectMessageLiterals(string source, Regex callStart, int argIndex, List<string> results)
    {
        foreach (Match m in callStart.Matches(source))
        {
            if (m.Groups["recv"].Success && NotScopedReceivers.Contains(m.Groups["recv"].Value)) continue;
            string? args = ExtractBalancedArgs(source, m.Index);
            if (args == null) continue;
            var parts = SplitTopLevelArgs(args);
            if (argIndex >= parts.Count) continue;
            string arg = parts[argIndex].Trim();
            if (arg.StartsWith("$\"") || arg.StartsWith("\"")) results.Add(arg);
        }
    }

    /// <summary>The message literal of every call a player may see on the console.</summary>
    public static List<string> ConsoleEligibleMessageLiterals(string source)
    {
        var results = new List<string>();
        CollectMessageLiterals(source, ConsoleEligibleCall, 1, results);
        CollectMessageLiterals(source, ScopedCall, 0, results);
        return results;
    }

    /// <summary>The trace detail literal of every WithTrace call.</summary>
    public static List<string> TraceDetailLiterals(string source)
    {
        var results = new List<string>();
        CollectMessageLiterals(source, TraceCall, 2, results);
        return results;
    }

    /// <summary>A console message opens with a capital or an interpolation hole, never a bare "Word:" leader.</summary>
    public static bool PassesSubjectFirstFence(string literal)
    {
        string body = literal.StartsWith("$\"") ? literal.Substring(2) : literal.StartsWith("\"") ? literal.Substring(1) : literal;
        if (body.Length == 0) return false;
        if (body[0] == '{') return true;
        if (!char.IsUpper(body[0])) return false;
        return !LeaderPrefix.IsMatch(body);
    }
}
