using System;
using System.IO;

namespace ModGates.Tests;

/// <summary>Walks up from the test bin folder to the first folder holding rules.json, the repo root.</summary>
internal static class RepoRoot
{
    public static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "rules.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("no rules.json found above the test bin folder");
    }
}
