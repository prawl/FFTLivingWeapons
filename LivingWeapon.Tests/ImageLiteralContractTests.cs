using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-387/LW-388 tripwire: the Denuvo-free build is ASLR-relocated, so a raw image address literal
/// (0x14xxxxxxx) that is used without <c>ImageBase.At(...)</c> reads the wrong memory on a real
/// launch while every unit test (which runs at the preferred base) stays green. This scans the
/// production project's source and fails on any such literal outside the exact allow-list. The
/// allow-list ratchets both ways (DocsContractTests idiom): an exempt file that no longer carries
/// a bare literal must be removed from the list. The no-bake half (LW-391) catches the second way
/// to lose the base: a field initializer that reads a rebased address once, at type-init.
/// </summary>
public class ImageLiteralContractTests
{
    // File name -> why a bare literal is acceptable there. EXACT set, both directions enforced.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ImageBase.cs"] = "holds the Preferred constant itself, the rebase origin",
        ["BodyDoubleSpike.cs"] = "LWDEV spikes re-anchor lazily per docs/PATCH_REANCHOR.md Step 7",
        ["NumeralSpike.cs"] = "LWDEV spikes re-anchor lazily per docs/PATCH_REANCHOR.md Step 7",
        ["StatusSpike.cs"] = "LWDEV spikes re-anchor lazily per docs/PATCH_REANCHOR.md Step 7",
    };

    // LW-388 A8: the PIN-space site tables. Each record's address is a documented preferred-base
    // pin that every consumer rebases through ImageBase.At at use (ExtendedSites.Build,
    // ListRelocation.Live, TemplateRelocation.Live), so a literal there is accepted ONLY as the
    // first argument of a record constructor `new(`; anywhere else in these files (a label, an
    // expression) it is a bare literal like any other. EXACT set, both directions enforced.
    private static readonly Dictionary<string, string> PinTables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ExtendedSites.cs"] = "BootSites/PostLoadSites: Site.Addr rebased in ExtendedSites.Build",
        ["ListRelocation.Sites.cs"] = "Sites: Site.Addr rebased by ListRelocation.Live",
        ["TemplateRelocation.Sites.cs"] = "RipFields: RipField.Addr rebased by TemplateRelocation.Live",
    };

    private static readonly Regex Literal = new(@"0x1_*[45][0-9A-Fa-f_]{7,}(?:UL|ul|U|u|L)?\b", RegexOptions.Compiled);

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
    /// the 0x14 image range and the dead 0x15 protector range, with or without digit separators or
    /// an integer suffix. Comments are ignored.</summary>
    internal static List<string> OffendingLiterals(string line, bool pinTable = false)
    {
        var hits = new List<string>();
        string code = StripLineComment(line);
        foreach (Match m in Literal.Matches(code))
        {
            string before = code.Substring(0, m.Index).TrimEnd();
            if (before.EndsWith("ImageBase.At(", StringComparison.Ordinal)) continue;
            if (pinTable && IsPinRecordStart(before)) continue;
            hits.Add(m.Value);
        }
        return hits;
    }

    // A pin-table record: the literal is the first argument of a target-typed `new(`.
    private static bool IsPinRecordStart(string before) => Regex.IsMatch(before, @"(?:^|[\s,{(])new\($");

    /// <summary>Lines (1-based) holding a literal that is not the direct argument of ImageBase.At(
    /// (or, in a pin table, the first argument of a record constructor).</summary>
    private static List<int> BareLiteralLines(string path)
    {
        var hits = new List<int>();
        bool pinTable = PinTables.ContainsKey(Path.GetFileName(path));
        string[] lines = File.ReadAllLines(path);
        for (int n = 0; n < lines.Length; n++)
            if (OffendingLiterals(lines[n], pinTable).Count > 0) hits.Add(n + 1);
        return hits;
    }

    /// <summary>Lines (1-based) holding a pin-table record literal (the first argument of `new(`).</summary>
    private static List<int> PinRecordLines(string path)
    {
        var hits = new List<int>();
        string[] lines = File.ReadAllLines(path);
        for (int n = 0; n < lines.Length; n++)
        {
            string code = StripLineComment(lines[n]);
            foreach (Match m in Literal.Matches(code))
                if (IsPinRecordStart(code.Substring(0, m.Index).TrimEnd())) { hits.Add(n + 1); break; }
        }
        return hits;
    }

    [Theory]
    [InlineData("ImageBase.At(0x140782A30)", 0)]
    [InlineData("ImageBase.At( 0x1411A7D10L )", 0)]
    [InlineData("ImageBase.At(x) + 0x141855CE0", 1)]
    [InlineData("0x150BF66DC", 1)]
    [InlineData("0x1_4078_2A30", 1)]
    [InlineData("0x140782A30UL", 1)]
    [InlineData("0x140782A30u", 1)]
    [InlineData("// 0x140782A30", 0)]
    [InlineData("new(0x140284800L, 0x01, Widen.HighByte, \"x\"),", 1)]
    public void OffendingLiterals_requires_the_literal_to_be_the_direct_At_argument(string line, int expected)
    {
        Assert.Equal(expected, OffendingLiterals(line).Count);
    }

    [Theory]
    [InlineData("        new(0x140284800L, 0x01, Widen.HighByte, \"cap\"),", 0)]
    [InlineData("    { new(0x14028617CL, 0x015EE3C0, -0x1E6, \"lea rsi\"),", 0)]
    [InlineData("        new(0x14028617CL, 0, 0, \"site 0x140286179\"),", 1)]
    [InlineData("        long a = 0x140284800L;", 1)]
    [InlineData("        Use(new(1, 0x140284800L));", 1)]
    [InlineData("        renew(0x140284800L);", 1)]
    public void Pin_tables_accept_a_literal_only_as_a_record_constructors_first_argument(string line, int expected)
    {
        Assert.Equal(expected, OffendingLiterals(line, pinTable: true).Count);
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
            $"{offenders.Count} bare 0x14xxxxxxx image literal(s) not wrapped in ImageBase.At( (ASLR would misread them): "
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

    [Fact]
    public void Pin_table_list_holds_only_files_that_still_carry_pin_records()
    {
        var files = ProductionFiles().ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        foreach (string name in PinTables.Keys)
        {
            if (!files.TryGetValue(name, out string? path) || PinRecordLines(path).Count == 0)
                stale.Add(name);
        }
        Assert.True(stale.Count == 0,
            "Pin-table file(s) no longer carry a pin record (remove the entry): " + string.Join(", ", stale));
    }

    // ---- the no-bake ratchet (LW-388 R6, closes LW-391) ----

    // A field declaration: const, static readonly, readonly static, or a plain static field with an
    // initializer. The type is a tuple group or a dotted/generic name, with optional array ranks and a
    // nullable mark; "=>" (an expression-bodied member) and "==" never count as an initializer.
    private static readonly Regex FieldDecl = new(
        @"\b(?:const|static\s+readonly|readonly\s+static|static)\s+(?:\([^)]*\)|[\w\.]+(?:<[^;=()]*>)?)(?:\[[,\s]*\])*\??\s+\w+\s*=(?![>=])",
        RegexOptions.Compiled);

    private static readonly Regex OffsetsAddressMember = new(@"(?:public|internal)\s+static\s+long\s+(\w+)\s*=>", RegexOptions.Compiled);

    // Base-dependent names outside Offsets.cs: the pins that live beside their consumers, and the two
    // former baked arrays (WeaponPalette.Banks, TemplateSeat.WeaponRegions) a static initializer must
    // not capture either. ImageBase.Base/At/Delta move with the base by definition.
    private static readonly string[] FixedBaseDependent =
    {
        "AbilityBase", "DecoyActionTable", "FnSetTextString", "BattleUnitsBase", "Rec8Addr", "Rec9Addr",
        "Banks", "WeaponRegions",
    };
    private static readonly string[] ImageBaseMembers = { "ImageBase.Base", "ImageBase.At", "ImageBase.Delta" };

    /// <summary>Every rebased ADDRESS member of Offsets.cs (expression-bodied static long), read from
    /// the source at run time so a new pin is covered the day it lands.</summary>
    internal static HashSet<string> OffsetsAddressNames()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), "LivingWeapon", "Offsets.cs"));
        return new HashSet<string>(OffsetsAddressMember.Matches(text).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
    }

    private static readonly Lazy<Regex> BaseDependentRef = new(() =>
    {
        var names = OffsetsAddressNames().Concat(FixedBaseDependent).Select(Regex.Escape);
        var imageBase = ImageBaseMembers.Select(Regex.Escape);
        return new Regex(@"\b(?:" + string.Join("|", names) + @")\b|" + string.Join("|", imageBase), RegexOptions.Compiled);
    });

    /// <summary>True when the declaration is a const / static readonly / static field whose initializer
    /// references a base-dependent address member (it would freeze the image base at type-init time).</summary>
    internal static bool BakesImageAddress(string declaration)
    {
        Match m = FieldDecl.Match(declaration);
        if (!m.Success) return false;
        string initializer = declaration.Substring(m.Index + m.Length);
        return BaseDependentRef.Value.IsMatch(initializer);
    }

    [Fact]
    public void The_address_member_set_is_read_from_Offsets()
    {
        // Guards the ratchet against vacuity: an empty set would pass every declaration.
        var names = OffsetsAddressNames();
        Assert.Contains("Slot0", names);
        Assert.Contains("RosterBase", names);
        Assert.Contains("WeaponPaletteBankA", names);
        Assert.DoesNotContain("AReraise", names);
        Assert.True(names.Count >= 70, $"only {names.Count} address members parsed from Offsets.cs");
    }

    [Theory]
    [InlineData("private static readonly long X = Offsets.RosterBase + 4;", true)]
    [InlineData("public const long Y = ImageBase.At(0x1);", true)]
    [InlineData("public static long Z => Offsets.RosterBase;", false)]
    [InlineData("private const int W = Offsets.RLevel;", false)]
    [InlineData("public const long Preferred = 0x140000000L;", false)]
    [InlineData("static readonly long[] B = { Offsets.WeaponPaletteBankA };", true)]
    [InlineData("static readonly int[] O = { Offsets.AReraise };", false)]
    [InlineData("static long X => Offsets.RosterBase;", false)]
    [InlineData("readonly static long Y = Offsets.Slot0;", true)]
    [InlineData("private static long Z = Offsets.Slot0;", true)]
    [InlineData("static readonly (long Addr, byte[] Bytes)[] T = { (Offsets.ThunkWeaponStat, new byte[] { 1 }) };", true)]
    [InlineData("static readonly nint P = (nint)Offsets.PickerOrderTemplate;", true)]
    [InlineData("internal static ulong U = (ulong)Barrage.AbilityBase;", true)]
    [InlineData("static readonly Region[] R = { new(Offsets.InventoryOrderTemplate, 4) };", true)]
    [InlineData("static (long Addr, byte[] Bytes)[] Thunks => new[] { (Offsets.ThunkWeaponStat, new byte[] { 1 }) };", false)]
    [InlineData("static bool Same(long a) => a == Offsets.Slot0;", false)]
    public void BakesImageAddress_flags_only_fields_reading_a_base_dependent_address(string decl, bool expected)
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
                if (!FieldDecl.IsMatch(StripLineComment(lines[n]))) continue;
                // Join to the first ';' so a multi-line array or tuple initializer is read whole.
                var decl = new System.Text.StringBuilder();
                for (int end = n; end < lines.Length; end++)
                {
                    string code = StripLineComment(lines[end]);
                    decl.Append(code).Append(' ');
                    if (code.Contains(';')) break;
                }
                if (BakesImageAddress(decl.ToString())) offenders.Add($"{name}:{n + 1}");
            }
        }
        Assert.True(offenders.Count == 0,
            "const/static field(s) bake a base-dependent image address at type-init (use a property or method): "
            + string.Join(", ", offenders));
    }
}
