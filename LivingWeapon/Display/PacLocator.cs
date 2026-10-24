using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LivingWeapon;

/// <summary>One place modded.pac might be, and the words the log uses for it.</summary>
internal readonly record struct PacCandidate(string Label, string Path);

/// <summary>Where to look for the game's modded.pac. The modloader merges it under the game
/// folder, taken to be the folder holding the running exe. The walk up from the mod folder only
/// finds it when Reloaded sits inside the game folder, so it is the fallback.</summary>
internal static class PacLocator
{
    internal const string ExeLabel = "beside the game exe";
    internal const string ModDirLabel = "three folders above the mod folder";

    internal static IReadOnlyList<PacCandidate> Candidates(string? exePath, string? modDir)
    {
        var list = new List<PacCandidate>();
        try
        {
            var dir = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(dir)) list.Add(new PacCandidate(ExeLabel, PacUnder(dir)));
        }
        catch { }
        try
        {
            var root = string.IsNullOrWhiteSpace(modDir) ? null : new DirectoryInfo(modDir).Parent?.Parent?.Parent;
            if (root != null)
            {
                var walk = PacUnder(root.FullName);
                if (list.Count == 0 || !string.Equals(list[0].Path, walk, StringComparison.OrdinalIgnoreCase))
                    list.Add(new PacCandidate(ModDirLabel, walk));
            }
        }
        catch { }
        return list;
    }

    internal static PacCandidate? FirstExisting(IReadOnlyList<PacCandidate> candidates, Func<string, bool> exists)
    {
        foreach (var c in candidates)
        {
            try { if (exists(c.Path)) return c; }
            catch { }
        }
        return null;
    }

    internal static string Describe(IReadOnlyList<PacCandidate> candidates) =>
        candidates.Count == 0 ? "no candidate path" : string.Join("; ", candidates.Select(c => $"{c.Label} ({c.Path})"));

    /// <summary>The running exe's path, with the main module as a second source. Null on any failure.</summary>
    internal static string? LiveExePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return path;
            using var proc = Process.GetCurrentProcess();
            var fallback = proc.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
        }
        catch { return null; }
    }

    private static string PacUnder(string gameRoot) =>
        Path.GetFullPath(Path.Combine(gameRoot, "data", "enhanced", "modded.pac"));
}
