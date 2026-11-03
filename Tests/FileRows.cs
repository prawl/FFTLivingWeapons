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
/// count as one file. A Python file under a folder rules.json lists in pythonProd joins, keyed by
/// its repo relative path. Every shared test that holds a file to its row reads the rows from here.
/// </summary>
internal static class FileRows
{
    /// <summary>Where the rows live and the one command that moves them, so a refusal is fixable in one edit.</summary>
    public static string WriteHint => $"Baseline: {Rules.Load().Baselines}/files.json, written by python {HealthScript} floor --write for a shrink, a new file or a gone file; the shared pre-commit runs it for you. Commit it with the change.";

    /// <summary>The owner's override, named the same way everywhere a row can refuse.</summary>
    public static string WidenHint => $"A wider row is the owner's call: python {HealthScript} floor --write --allow-worse --card <id>, citing a Backlog row that says why.";

    /// <summary>The health tool's path as a mod sees it, or as this repo sees its own.</summary>
    public static string HealthScript => File.Exists(Path.Combine(RepoRoot.Locate(), "gates", "tools", "health.py")) ? "gates/tools/health.py" : "tools/health.py";

    private static readonly Regex TypeDecl = new(
        @"^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|unsafe|readonly|ref|file|new)\s+)*(partial\s+)?(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(\w+)",
        RegexOptions.Compiled);

    private sealed record Record(string[] Columns, Dictionary<string, int[]> Files);

    public static bool IsComment(string trimmed, bool python = false)
        => python ? trimmed.StartsWith("#") : trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("*");

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

    /// <summary>Every production file with its key, path, text and language, build output skipped, in path order.</summary>
    public static List<(string Key, string Path, string Text, bool Python)> ProductionFiles()
    {
        string root = Rules.Load().ProdDir;
        var files = new List<(string, string, string, bool)>();
        foreach (var f in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel.StartsWith("bin/") || rel.StartsWith("obj/") || rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            string text = File.ReadAllText(f);
            string? partial = PartialType(text);
            files.Add((partial is null ? rel : "partial " + partial, rel, text, false));
        }
        string repo = RepoRoot.Locate();
        foreach (var folder in Rules.Load().PythonProd)
        {
            string dir = Path.Combine(repo, folder.TrimEnd('/'));
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.py", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(repo, f).Replace('\\', '/');
                if (rel.Split('/').Any(part => part is "__pycache__" or "bin" or "obj")) continue;
                files.Add((rel, rel, File.ReadAllText(f), true));
            }
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
