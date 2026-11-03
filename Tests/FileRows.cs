using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModGates.Tests;

/// <summary>
/// The per-file rows health.py writes to docs/health/files.json, keyed the same way here. A key is
/// the path below the production folder, or "partial Type" for the parts of a partial type, which
/// count as one file. Every shared test that holds a file to its row reads the rows from here.
/// </summary>
internal static class FileRows
{
    public const string WriteHint = "Run health.py floor --write under the gates folder, then commit docs/health/files.json with this change.";

    private static readonly Regex TypeDecl = new(
        @"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|unsafe|readonly|ref|file|new)\s+)*(partial\s+)?(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(\w+)",
        RegexOptions.Compiled);

    private sealed record Record(string[] Columns, Dictionary<string, int[]> Files);

    public static bool IsComment(string trimmed) => trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*");

    /// <summary>The type name when the file's first type declaration is partial, else null.</summary>
    public static string? PartialType(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            if (IsComment(raw.Trim())) continue;
            var m = TypeDecl.Match(raw);
            if (m.Success) return m.Groups[1].Success ? m.Groups[2].Value : null;
        }
        return null;
    }

    /// <summary>Every production file with its key and text, build output skipped, in path order.</summary>
    public static List<(string Key, string Path, string Text)> ProductionFiles()
    {
        string root = Rules.Load().ProdDir;
        var files = new List<(string, string, string)>();
        foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/") || rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            string text = File.ReadAllText(f);
            string? partial = PartialType(text);
            files.Add((partial is null ? rel : "partial " + partial, rel, text));
        }
        return files;
    }

    /// <summary>The committed rows as key to column to value; empty when the repo has no record yet.</summary>
    public static Dictionary<string, Dictionary<string, int>> Load()
    {
        var rows = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        string path = Rules.Load().BaselinePath("files.json");
        if (!File.Exists(path)) return rows;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var rec = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), options);
        if (rec is null) return rows;
        foreach (var (key, values) in rec.Files)
            rows[key] = rec.Columns.Zip(values).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
        return rows;
    }
}
