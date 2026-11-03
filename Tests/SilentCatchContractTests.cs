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
    private static readonly Regex SilentExcept = new(@"^[ \t]*except\b[^\n]*:[ \t]*(?:#[^\n]*)?\n(?:[ \t]*#[^\n]*\n)*[ \t]*pass\b", RegexOptions.Compiled | RegexOptions.Multiline);

    private static Dictionary<string, int> CountsPerRow()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, _, text, python) in FileRows.ProductionFiles())
            counts[key] = counts.GetValueOrDefault(key) + (python ? SilentExcept : EmptyCatch).Matches(text.Replace("\r\n", "\n")).Count;
        return counts;
    }

    [Theory]
    [InlineData("try:\n    x()\nexcept OSError:\n    pass\n", 1)]
    [InlineData("try:\n    x()\nexcept OSError:  # why\n    # still why\n    pass\n", 1)]
    [InlineData("try:\n    x()\nexcept OSError as exc:\n    log(exc)\n", 0)]
    [InlineData("try:\n    x()\nexcept OSError:\n    passed = True\n", 0)]
    public void A_silent_except_is_one_whose_body_is_only_pass(string text, int expected)
        => Assert.Equal(expected, SilentExcept.Matches(text).Count);

    [Fact]
    public void No_production_file_has_an_empty_catch()
    {
        var rows = FileRows.Load();
        var over = CountsPerRow().Where(kv => kv.Value > (rows.TryGetValue(kv.Key, out var row) ? row.GetValueOrDefault("catches") : 0))
            .Select(kv => $"{kv.Key}: {kv.Value} (its row allows {(rows.TryGetValue(kv.Key, out var row) ? row.GetValueOrDefault("catches") : 0)})").ToList();
        Assert.True(over.Count == 0, "Empty catches found (note the fault or log it, never swallow it):\n" + string.Join("\n", over)
            + "\nThe catches column of the row only shrinks. " + FileRows.WriteHint);
    }
}
