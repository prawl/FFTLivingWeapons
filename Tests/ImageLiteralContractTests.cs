using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace ModGates.Tests;

/// <summary>
/// The address tripwire. The game loads at a different address every launch, so a raw image
/// address in code reads the wrong memory live while every unit test stays green. A literal may
/// only sit inside ImageBase.At or, in a pin table, as a record's first argument. Every wrapped
/// literal is one of the repo's frozen pins, and no field bakes a rebased address at type init.
/// The allow lists, pin tables and pins come from rules.json and ratchet both ways.
/// </summary>
public class ImageLiteralContractTests
{
    private static readonly Regex Literal = new(@"0x1_*[45][0-9A-Fa-f_]{7,}(?:UL|ul|U|u|L)?\b", RegexOptions.Compiled);
    private static readonly Regex FieldDecl = new(
        @"\b(?:const|static\s+readonly|readonly\s+static|static)\s+(?:\([^)]*\)|[\w\.]+(?:<[^;=()]*>)?)(?:\[[,\s]*\])*\??\s+\w+\s*=(?![>=])",
        RegexOptions.Compiled);
    private static readonly Regex OffsetsAddressMember = new(@"(?:public|internal)\s+static\s+long\s+(\w+)\s*=>", RegexOptions.Compiled);
    private static readonly string[] ImageBaseMembers = { "ImageBase.Base", "ImageBase.At", "ImageBase.AtRva", "ImageBase.Delta" };

    private static string StripLineComment(string line)
    {
        int i = line.IndexOf("//", StringComparison.Ordinal);
        return i < 0 ? line : line.Substring(0, i);
    }

    private static IEnumerable<string> ProductionFiles()
    {
        string root = Rules.Load().ProdDir;
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(f =>
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            return !rel.StartsWith("bin/") && !rel.StartsWith("obj/");
        });
    }

    private static bool IsAtArgument(string before) => before.EndsWith("ImageBase.At(", StringComparison.Ordinal);
    private static bool IsPinRecordStart(string before) => Regex.IsMatch(before, @"(?:^|[\s,{(])new\($");

    /// <summary>The literals on one line that are not the direct argument of ImageBase.At, or of a record in a pin table.</summary>
    internal static List<string> OffendingLiterals(string line, bool pinTable = false)
    {
        var hits = new List<string>();
        string code = StripLineComment(line);
        foreach (Match m in Literal.Matches(code))
        {
            string before = code.Substring(0, m.Index).TrimEnd();
            if (IsAtArgument(before)) continue;
            if (pinTable && IsPinRecordStart(before)) continue;
            hits.Add(m.Value);
        }
        return hits;
    }

    /// <summary>The literals on one line that are the direct argument of ImageBase.At, normalised.</summary>
    internal static List<string> WrappedLiterals(string line)
    {
        var hits = new List<string>();
        string code = StripLineComment(line);
        foreach (Match m in Literal.Matches(code))
            if (IsAtArgument(code.Substring(0, m.Index).TrimEnd())) hits.Add(NormalisePin(m.Value));
        return hits;
    }

    internal static string NormalisePin(string literal) => "0x" + Regex.Replace(literal.Substring(2), @"[_ULul]", "").ToUpperInvariant();

    private static List<int> BareLiteralLines(string path)
    {
        bool pinTable = Rules.Load().PinTables.ContainsKey(Path.GetFileName(path));
        string[] lines = File.ReadAllLines(path);
        return Enumerable.Range(0, lines.Length).Where(n => OffendingLiterals(lines[n], pinTable).Count > 0).Select(n => n + 1).ToList();
    }

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
    [InlineData("public static long X => ImageBase.At(0x141853CE0);", 0)]
    [InlineData("private const long Base = 0x141853CE0;", 1)]
    [InlineData("var a = ImageBase.At( 0x14186AF68 );", 0)]
    [InlineData("// 0x141853CE0 in a comment is fine", 0)]
    [InlineData("long dead = 0x15F2EA40FL;", 1)]
    [InlineData("int rva = 0x31F130;", 0)]
    public void Literal_detection_examples(string line, int expected) => Assert.Equal(expected, OffendingLiterals(line).Count);

    [Theory]
    [InlineData("ImageBase.At(0x140782A30)", 0)]
    [InlineData("ImageBase.At( 0x1411A7D10L )", 0)]
    [InlineData("ImageBase.At(x) + 0x141855CE0", 1)]
    [InlineData("0x150BF66DC", 1)]
    [InlineData("0x1_4078_2A30", 1)]
    [InlineData("0x140782A30UL", 1)]
    [InlineData("0x140782A30u", 1)]
    [InlineData("// 0x140782A30", 0)]
    [InlineData("new(0x140284800L, 0x01, \"x\"),", 1)]
    [InlineData("new(0x140284800L, 0x01, Widen.HighByte, \"x\"),", 1)]
    [InlineData("ImageBase.AtRva(0x141853CE0)", 1)]
    public void OffendingLiterals_requires_the_literal_to_be_the_direct_At_argument(string line, int expected)
        => Assert.Equal(expected, OffendingLiterals(line).Count);

    [Theory]
    [InlineData("        new(0x140284800L, 0x01, Widen.HighByte, \"cap\"),", 0)]
    [InlineData("    { new(0x14028617CL, 0x015EE3C0, -0x1E6, \"lea rsi\"),", 0)]
    [InlineData("        new(0x14028617CL, 0, 0, \"site 0x140286179\"),", 1)]
    [InlineData("        long a = 0x140284800L;", 1)]
    [InlineData("        Use(new(1, 0x140284800L));", 1)]
    [InlineData("        renew(0x140284800L);", 1)]
    public void Pin_tables_accept_a_literal_only_as_a_record_constructors_first_argument(string line, int expected)
        => Assert.Equal(expected, OffendingLiterals(line, pinTable: true).Count);

    [Theory]
    [InlineData("0x141853CE0", "0x141853CE0")]
    [InlineData("0x141853ce0L", "0x141853CE0")]
    [InlineData("0x1_4185_3CE0UL", "0x141853CE0")]
    [InlineData("0x14186AF68u", "0x14186AF68")]
    public void NormalisePin_folds_case_suffix_and_separators(string literal, string expected) => Assert.Equal(expected, NormalisePin(literal));

    [Fact]
    public void WrappedLiterals_reports_only_the_direct_At_argument()
    {
        Assert.Equal(new[] { "0x141853CE0" }, WrappedLiterals("public static long X => ImageBase.At(0x141853CE0);"));
        Assert.Empty(WrappedLiterals("private const long Base = 0x141853CE0;"));
        Assert.Empty(WrappedLiterals("// ImageBase.At(0x141853CE0)"));
        Assert.Equal(new[] { "0x14186AF68" }, WrappedLiterals("ImageBase.At(x) + ImageBase.At( 0x1_4186_AF68UL )"));
    }

    [Fact]
    public void No_bare_image_address_literal_outside_the_allow_list()
    {
        var allowed = Rules.Load().ImageAllowlist;
        var offenders = new List<string>();
        foreach (string f in ProductionFiles())
        {
            string name = Path.GetFileName(f);
            if (allowed.ContainsKey(name)) continue;
            foreach (int line in BareLiteralLines(f)) offenders.Add($"{name}:{line}");
        }
        Assert.True(offenders.Count == 0, $"{offenders.Count} bare image literal(s) not wrapped in ImageBase.At( (the live base would misread them): " + string.Join(", ", offenders));
    }

    [Fact]
    public void Allow_list_holds_only_files_that_still_need_the_exemption()
    {
        var files = ProductionFiles().ToLookup(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        var stale = Rules.Load().ImageAllowlist.Keys.Where(name => !files[name].Any(path => BareLiteralLines(path).Count > 0)).ToList();
        Assert.True(stale.Count == 0, "Allow-listed file(s) no longer carry a bare image literal (remove the exemption): " + string.Join(", ", stale));
    }

    [Fact]
    public void Pin_table_list_holds_only_files_that_still_carry_pin_records()
    {
        var files = ProductionFiles().ToLookup(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        var stale = Rules.Load().PinTables.Keys.Where(name => !files[name].Any(path => PinRecordLines(path).Count > 0)).ToList();
        Assert.True(stale.Count == 0, "Pin-table file(s) no longer carry a pin record (remove the entry): " + string.Join(", ", stale));
    }

    private static List<(string Pin, string Where)> WrappedPinsInProduction()
    {
        var found = new List<(string, string)>();
        foreach (string f in ProductionFiles())
        {
            string[] lines = File.ReadAllLines(f);
            for (int n = 0; n < lines.Length; n++)
                foreach (string pin in WrappedLiterals(lines[n])) found.Add((pin, $"{Path.GetFileName(f)}:{n + 1}"));
        }
        return found;
    }

    [Fact]
    public void Every_wrapped_literal_is_one_of_the_frozen_pins()
    {
        var pins = Rules.Load().Pins;
        var unknown = WrappedPinsInProduction().Where(p => !pins.ContainsKey(p.Pin)).Select(p => $"{p.Pin} at {p.Where}").ToList();
        Assert.True(unknown.Count == 0, "ImageBase.At( carries an address that is not in the frozen pins (verify it, then add it to pins with what it is): " + string.Join(", ", unknown));
    }

    [Fact]
    public void Frozen_pin_set_holds_only_pins_production_still_uses()
    {
        var live = new HashSet<string>(WrappedPinsInProduction().Select(p => p.Pin), StringComparer.Ordinal);
        var stale = Rules.Load().Pins.Keys.Where(k => !live.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Frozen pin(s) no longer appear inside ImageBase.At( anywhere (remove them from pins): " + string.Join(", ", stale));
    }

    [Fact]
    public void Frozen_pin_keys_are_normalised()
    {
        var pins = Rules.Load().Pins;
        foreach (string key in pins.Keys) Assert.Equal(NormalisePin(key), key);
        if (WrappedPinsInProduction().Count > 0) Assert.True(pins.Count > 0, "production wraps addresses but no pin is frozen");
    }

    /// <summary>The address members of the repo's Offsets file, read from source so a new pin is covered the day it lands.</summary>
    internal static HashSet<string> OffsetsAddressNames()
    {
        var rules = Rules.Load();
        string path = Path.Combine(RepoRoot.Locate(), rules.OffsetsFile ?? Path.Combine(rules.ProdName, "Offsets.cs"));
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        return new HashSet<string>(OffsetsAddressMember.Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
    }

    private static Regex BaseDependentRef(IEnumerable<string> names)
    {
        var all = names.Concat(Rules.Load().BaseDependentNames).Select(Regex.Escape).ToList();
        string named = all.Count == 0 ? "(?!x)x" : @"\b(?:" + string.Join("|", all) + @")\b";
        return new Regex(named + "|" + string.Join("|", ImageBaseMembers.Select(Regex.Escape)), RegexOptions.Compiled);
    }

    /// <summary>True when a const or static field's initializer reads a base dependent address.</summary>
    internal static bool BakesImageAddress(string declaration, IEnumerable<string> addressNames)
    {
        Match m = FieldDecl.Match(declaration);
        if (!m.Success) return false;
        return BaseDependentRef(addressNames).IsMatch(declaration.Substring(m.Index + m.Length));
    }

    private static readonly string[] SampleNames =
    {
        "BattleUnitsBase", "ActorPtr", "BattleMode", "BattleMapId", "RosterBase", "Slot0", "WeaponPaletteBankA",
        "ThunkWeaponStat", "PickerOrderTemplate", "InventoryOrderTemplate", "AbilityBase",
    };

    [Fact]
    public void The_address_member_set_is_read_from_Offsets()
    {
        var names = OffsetsAddressNames();
        var rules = Rules.Load();
        bool hasOffsets = File.Exists(Path.Combine(RepoRoot.Locate(), rules.OffsetsFile ?? Path.Combine(rules.ProdName, "Offsets.cs")));
        if (hasOffsets) Assert.True(names.Count > 0, "Offsets.cs exists but no address member was parsed from it");
        else Assert.Empty(names);
    }

    [Theory]
    [InlineData("private static readonly long X = Offsets.BattleUnitsBase + 4;", true)]
    [InlineData("public const long Y = ImageBase.At(0x1);", true)]
    [InlineData("public static long Z => Offsets.BattleUnitsBase;", false)]
    [InlineData("private const int W = Offsets.UnitStride;", false)]
    [InlineData("public const long Preferred = 0x140000000L;", false)]
    [InlineData("static readonly long[] B = { Offsets.ActorPtr };", true)]
    [InlineData("static readonly int[] O = { Offsets.UnitHp };", false)]
    [InlineData("static long X => Offsets.BattleMode;", false)]
    [InlineData("readonly static long Y = Offsets.ActorPtr;", true)]
    [InlineData("private static long Z = Offsets.ActorPtr;", true)]
    [InlineData("static readonly (long Addr, byte[] Bytes)[] T = { (Offsets.BattleMode, new byte[] { 1 }) };", true)]
    [InlineData("static readonly nint P = (nint)Offsets.BattleUnitsBase;", true)]
    [InlineData("internal static long U = ImageBase.AtRva(0x30C8A6);", true)]
    [InlineData("internal static long V = ImageBase.Base + 0x10;", true)]
    [InlineData("static bool Same(long a) => a == Offsets.ActorPtr;", false)]
    [InlineData("private static readonly string Tag = \"BattleUnitsBaseline\";", false)]
    [InlineData("private static readonly long X = Offsets.RosterBase + 4;", true)]
    [InlineData("public static long Z => Offsets.RosterBase;", false)]
    [InlineData("private const int W = Offsets.RLevel;", false)]
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
    [InlineData("private static readonly long X = Offsets.Slot0 + 4;", true)]
    [InlineData("public static long Z => Offsets.Slot0;", false)]
    [InlineData("private const int W = Offsets.UnitArraySlots;", false)]
    public void BakesImageAddress_flags_only_fields_reading_a_base_dependent_address(string decl, bool expected)
        => Assert.Equal(expected, BakesImageAddress(decl, SampleNames));

    [Fact]
    public void No_production_field_bakes_an_image_address()
    {
        var allowed = Rules.Load().ImageAllowlist;
        var names = OffsetsAddressNames();
        var offenders = new List<string>();
        foreach (string f in ProductionFiles())
        {
            string name = Path.GetFileName(f);
            if (allowed.ContainsKey(name)) continue;
            string[] lines = File.ReadAllLines(f);
            for (int n = 0; n < lines.Length; n++)
            {
                if (!FieldDecl.IsMatch(StripLineComment(lines[n]))) continue;
                var decl = new StringBuilder();
                for (int end = n; end < lines.Length; end++)
                {
                    string code = StripLineComment(lines[end]);
                    decl.Append(code).Append(' ');
                    if (code.Contains(';')) break;
                }
                if (BakesImageAddress(decl.ToString(), names)) offenders.Add($"{name}:{n + 1}");
            }
        }
        Assert.True(offenders.Count == 0, "const/static field(s) bake a base-dependent image address at type-init (use a property or method): " + string.Join(", ", offenders));
    }
}
