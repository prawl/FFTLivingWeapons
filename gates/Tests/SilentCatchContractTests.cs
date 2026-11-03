using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// Holds the empty catch count at zero. A file still carrying some on the day the rules arrive has
/// the count in its row in files.json, and the ratchet makes that row shrink. The regex is the one
/// health.py counts with, so a catch holding only a comment still counts.
/// </summary>
public class SilentCatchContractTests
{
    private static readonly Regex EmptyCatch = new(@"catch(\s*\([^)]*\))?(\s*when\s*\([^)]*\))?\s*\{\s*(/\*[^*]*\*/)?\s*\}", RegexOptions.Compiled);

    private static Dictionary<string, int> CountsPerRow()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, _, text) in FileRows.ProductionFiles())
            counts[key] = counts.GetValueOrDefault(key) + EmptyCatch.Matches(text).Count;
        return counts;
    }

    [Fact]
    public void No_production_file_has_an_empty_catch()
    {
        var rows = FileRows.Load();
        var over = CountsPerRow().Where(kv => kv.Value > (rows.TryGetValue(kv.Key, out var row) ? row.GetValueOrDefault("catches") : 0))
            .Select(kv => $"{kv.Key}: {kv.Value} (its row allows {(rows.TryGetValue(kv.Key, out var row) ? row.GetValueOrDefault("catches") : 0)})").ToList();
        Assert.True(over.Count == 0, "Empty catches found (note the fault or log it, never swallow it):\n" + string.Join("\n", over));
    }
}
