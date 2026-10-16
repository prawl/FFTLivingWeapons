using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-387 tripwire: the Denuvo-free build is ASLR-relocated, so a raw image address literal
/// (0x14xxxxxxx) that is used without <c>ImageBase.At(...)</c> reads the wrong memory on a real
/// launch while every unit test (which runs at the preferred base) stays green. This scans the
/// production project's source and fails on any such literal outside the exact allow-list. The
/// allow-list ratchets both ways (DocsContractTests idiom): an exempt file that no longer carries
/// a bare literal must be removed from the list.
/// </summary>
public class ImageLiteralContractTests
{
    // File name -> why a bare literal is acceptable there. EXACT set, both directions enforced.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImageBase.cs"] = "holds the Preferred constant itself, the rebase origin",
        ["TreasureDb.cs"] = "classifies baked data values, not live reads; Treasure Master is auto-disarmed on this build by its own baked key (PATCH_REANCHOR Step 7 default)",
        ["BodyDoubleSpike.cs"] = "LWDEV spikes re-anchor lazily per docs/PATCH_REANCHOR.md Step 7",
        ["NumeralSpike.cs"] = "LWDEV spikes re-anchor lazily per docs/PATCH_REANCHOR.md Step 7",
        ["StatusSpike.cs"] = "LWDEV spikes re-anchor lazily per docs/PATCH_REANCHOR.md Step 7",
    };

    private static readonly Regex Literal = new(@"0x1_*[45][0-9A-Fa-f_]{7,}L?\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "TODO.md")) &&
                Directory.Exists(Path.Combine(dir.FullName, "LivingWeapon")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("repo root (docs/TODO.md + LivingWeapon/) not found above the test bin dir");
    }

    // Drops a trailing // comment (this also covers /// doc lines). A "//" inside a string literal
    // would be cut early, which can only hide a literal after it; no pin line carries one.
    private static string StripLineComment(string line)
    {
        int i = line.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? line : line.Substring(0, i);
    }

    private static IEnumerable<string> ProductionFiles()
    {
        string root = Path.Combine(RepoRoot(), "LivingWeapon");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.StartsWith("bin/") && !rel.StartsWith("obj/");
            });
    }

    /// <summary>The image-address literals on one source line that are not the DIRECT argument of
    /// ImageBase.At( (text immediately before the literal, whitespace ignored, ends with it). Covers
    /// the 0x14 image range and the dead 0x15 protector range, with or without digit separators.
    /// Comments are ignored.</summary>
    internal static List<string> OffendingLiterals(string line)
    {
        var hits = new List<string>();
        string code = StripLineComment(line);
        foreach (Match m in Literal.Matches(code))
        {
            string before = code.Substring(0, m.Index).TrimEnd();
            if (!before.EndsWith("ImageBase.At(", StringComparison.Ordinal)) hits.Add(m.Value);
        }
        return hits;
    }

    /// <summary>Lines (1-based) holding a literal that is not the direct argument of ImageBase.At(.</summary>
    private static List<int> BareLiteralLines(string path)
    {
        var hits = new List<int>();
        string[] lines = File.ReadAllLines(path);
        for (int n = 0; n < lines.Length; n++)
            if (OffendingLiterals(lines[n]).Count > 0) hits.Add(n + 1);
        return hits;
    }

    [Theory]
    [InlineData("ImageBase.At(0x140782A30)", 0)]
    [InlineData("ImageBase.At( 0x1411A7D10L )", 0)]
    [InlineData("ImageBase.At(x) + 0x141855CE0", 1)]
    [InlineData("0x150BF66DC", 1)]
    [InlineData("0x1_4078_2A30", 1)]
    [InlineData("// 0x140782A30", 0)]
    public void OffendingLiterals_requires_the_literal_to_be_the_direct_At_argument(string line, int expected)
    {
        Assert.Equal(expected, OffendingLiterals(line).Count);
    }

    [Fact]
    public void No_bare_image_address_literal_outside_the_allow_list()
    {
        var offenders = new List<string>();
        foreach (string f in ProductionFiles())
        {
            string name = Path.GetFileName(f);
            if (Allowed.ContainsKey(name)) continue;
            foreach (int line in BareLiteralLines(f)) offenders.Add($"{name}:{line}");
        }
        Assert.True(offenders.Count == 0,
            "Bare 0x14xxxxxxx image literal(s) not wrapped in ImageBase.At( (ASLR would misread them): "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Allow_list_holds_only_files_that_still_need_the_exemption()
    {
        var files = ProductionFiles().ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        foreach (string name in Allowed.Keys)
        {
            if (!files.TryGetValue(name, out string? path) || BareLiteralLines(path).Count == 0)
                stale.Add(name);
        }
        Assert.True(stale.Count == 0,
            "Allow-listed file(s) no longer carry a bare image literal (remove the exemption): " + string.Join(", ", stale));
    }

    private static readonly Regex LongDecl = new(@"\b(const|static\s+readonly)\s+long\s+\w+\s*=", RegexOptions.Compiled);

    // Names whose value depends on the live image base; a const/static readonly long that reads one
    // freezes the value at first touch (static readonly) or cannot compile (const), and silently
    // ignores a later ImageBase.Set.
    private static readonly string[] BaseDependent =
    {
        "Offsets.", "ImageBase.", "AbilityBase", "DecoyActionTable", "FnSetTextString",
        "BattleUnitsBase", "Rec8Addr", "Rec9Addr",
    };

    /// <summary>True when the declaration is a const/static readonly long whose initializer references
    /// a base-dependent pin (it would bake the image address at type-init time).</summary>
    internal static bool BakesImageAddress(string declaration)
    {
        Match m = LongDecl.Match(declaration);
        if (!m.Success) return false;
        string initializer = declaration.Substring(m.Index + m.Length);
        return BaseDependent.Any(n => initializer.Contains(n, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("private static readonly long X = Offsets.RosterBase + 4;", true)]
    [InlineData("public const long Y = ImageBase.At(0x1);", true)]
    [InlineData("public static long Z => Offsets.RosterBase;", false)]
    [InlineData("private const int W = Offsets.RLevel;", false)]
    [InlineData("public const long Preferred = 0x140000000L;", false)]
    public void BakesImageAddress_flags_only_long_fields_reading_a_base_dependent_pin(string decl, bool expected)
    {
        Assert.Equal(expected, BakesImageAddress(decl));
    }

    [Fact]
    public void No_production_field_bakes_an_image_address()
    {
        var offenders = new List<string>();
        foreach (string f in ProductionFiles())
        {
            string name = Path.GetFileName(f);
            if (Allowed.ContainsKey(name)) continue;
            string[] lines = File.ReadAllLines(f);
            for (int n = 0; n < lines.Length; n++)
            {
                if (!LongDecl.IsMatch(StripLineComment(lines[n]))) continue;
                var decl = new System.Text.StringBuilder();
                int end = n;
                for (; end < lines.Length; end++)
                {
                    string code = StripLineComment(lines[end]);
                    decl.Append(code).Append(' ');
                    if (code.Contains(';')) break;
                }
                if (BakesImageAddress(decl.ToString())) offenders.Add($"{name}:{n + 1}");
            }
        }
        Assert.True(offenders.Count == 0,
            "const/static readonly long field(s) bake a base-dependent image address (use a property): "
            + string.Join(", ", offenders));
    }
}
