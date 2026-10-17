using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// A dev hotkey fired during a drill on a dev deploy and nothing mechanical kept a key read out of a
/// release build. This scans the production source and fails on any key read API that sits outside a
/// bare <c>#if LWDEV</c> region, then checks the referenced binary itself. The walker reads the
/// directive stack by hand, so a malformed file throws instead of passing quietly.
/// </summary>
public class HotkeyContractTests
{
    private static readonly Regex KeyRead = new(
        @"\b(GetAsyncKeyState|GetKeyState|GetKeyboardState|RegisterHotKey|SetWindowsHookEx[AW]?|GetRawInputData|RegisterRawInputDevices|ReadConsoleInput[AW]?)\b|\bConsole\.(ReadKey|KeyAvailable)\b",
        RegexOptions.Compiled);

    private static readonly HashSet<string> KnownDevFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "BodyDoubleSpike.cs", "NumeralSpike.cs", "ProvokeSpike.cs", "StatusSpike.cs",
    };

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

    private static string Label(string path) => "LivingWeapon/" + Path.GetFileName(path);

    private static (List<int> Guarded, List<int> Offenders) Walk(string[] lines)
    {
        var guarded = new List<int>();
        var offenders = new List<int>();
        var stack = new List<bool>();
        for (int n = 0; n < lines.Length; n++)
        {
            int no = n + 1;
            // ReadAllLines already strips a BOM, the trim covers a later raw read.
            string text = lines[n].TrimStart().TrimStart('\uFEFF').TrimStart();
            if (text.StartsWith('#'))
            {
                string body = text.Substring(1);
                int cut = body.IndexOf("//", StringComparison.Ordinal);
                if (cut >= 0) body = body.Substring(0, cut);
                body = body.Trim();
                int sp = body.IndexOfAny(new[] { ' ', '\t' });
                string keyword = sp < 0 ? body : body.Substring(0, sp);
                string expr = sp < 0 ? "" : body.Substring(sp).Trim();
                switch (keyword)
                {
                    case "if":
                        stack.Add(expr == "LWDEV");
                        break;
                    case "elif":
                    case "else":
                        if (stack.Count == 0) throw new InvalidOperationException($"line {no}: unbalanced directive");
                        stack[stack.Count - 1] = false;
                        break;
                    case "endif":
                        if (stack.Count == 0) throw new InvalidOperationException($"line {no}: unbalanced directive");
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    case "define":
                    case "undef":
                        if (expr == "LWDEV") offenders.Add(no);
                        break;
                }
                continue;
            }
            if (text.StartsWith("//") || text.StartsWith("*")) continue;
            if (!KeyRead.IsMatch(lines[n])) continue;
            if (stack.Contains(true)) guarded.Add(no); else offenders.Add(no);
        }
        if (stack.Count > 0) throw new InvalidOperationException($"line {lines.Length}: unbalanced directive");
        return (guarded, offenders);
    }

    internal static List<int> UnguardedKeyReadLines(string[] lines) => Walk(lines).Offenders;

    internal static int GuardedKeyReadCount(string[] lines) => Walk(lines).Guarded.Count;

    private static string[] Src(string joined) => joined.Split('\n');

    [Theory]
    [InlineData("#if LWDEV\nx = GetAsyncKeyState(1);\n#endif", 0)]
    [InlineData("x = GetAsyncKeyState(1);", 1)]
    [InlineData("#if LWDEV\n#else\nx = GetAsyncKeyState(1);\n#endif", 1)]
    [InlineData("#if DEBUG\nx = GetAsyncKeyState(1);\n#endif", 1)]
    [InlineData("#if !LWDEV\nx = GetAsyncKeyState(1);\n#endif", 1)]
    [InlineData("#if LWDEV || DEBUG\nx = GetAsyncKeyState(1);\n#endif", 1)]
    [InlineData("#if LWDEV\n#if DEBUG\nx = GetAsyncKeyState(1);\n#endif\n#endif", 0)]
    [InlineData("#if LWDEV // dev only\nx = GetAsyncKeyState(1);\n#endif", 0)]
    [InlineData("\uFEFF#if LWDEV\nx = GetAsyncKeyState(1);\n#endif", 0)]
    [InlineData("// GetAsyncKeyState lives in the spikes", 0)]
    [InlineData("    /// <summary>GetAsyncKeyState</summary>", 0)]
    [InlineData("x = 1; // GetAsyncKeyState", 1)]
    [InlineData("x = Foo(\"http://a\"); GetAsyncKeyState(1);", 1)]
    [InlineData("#define LWDEV\n#if LWDEV\nx = GetAsyncKeyState(1);\n#endif", 1)]
    [InlineData("#undef LWDEV\n#if LWDEV\nx = GetAsyncKeyState(1);\n#endif", 1)]
    [InlineData("h = SetWindowsHookEx(13, cb, 0, 0);", 1)]
    [InlineData("if (Console.KeyAvailable) {}", 1)]
    public void UnguardedKeyReadLines_reads_the_directive_stack(string source, int expectedOffenders)
    {
        Assert.Equal(expectedOffenders, UnguardedKeyReadLines(Src(source)).Count);
    }

    [Fact]
    public void A_define_of_LWDEV_reports_its_own_line()
    {
        var hits = UnguardedKeyReadLines(Src("#define LWDEV\n#if LWDEV\nx = GetAsyncKeyState(1);\n#endif"));
        Assert.Equal(new[] { 1 }, hits);
    }

    [Fact]
    public void An_endif_with_nothing_open_throws()
    {
        Assert.Throws<InvalidOperationException>(() => UnguardedKeyReadLines(Src("#endif")));
    }

    [Fact]
    public void An_if_that_never_closes_throws()
    {
        Assert.Throws<InvalidOperationException>(() => UnguardedKeyReadLines(Src("#if LWDEV\nx = 1;")));
    }

    [Fact]
    public void GuardedKeyReadCount_counts_only_fenced_hits()
    {
        var lines = Src("#if LWDEV\nx = GetAsyncKeyState(1);\n#endif\nx = GetAsyncKeyState(1);");
        Assert.Equal(1, GuardedKeyReadCount(lines));
        Assert.Single(UnguardedKeyReadLines(lines));
    }

    [Fact]
    public void No_key_read_API_outside_an_LWDEV_region()
    {
        var bad = new List<string>();
        foreach (string f in ProductionFiles())
            foreach (int line in UnguardedKeyReadLines(File.ReadAllLines(f)))
                bad.Add($"{Label(f)}:{line}");
        Assert.True(bad.Count == 0,
            "A key read API sits outside a bare #if LWDEV region, so a player could press it. " +
            "Wrap the whole file in #if LWDEV or delete the poll; a #define LWDEV must go. Offenders: " +
            string.Join(", ", bad));
    }

    [Fact]
    public void Guarded_key_reads_live_in_exactly_the_known_dev_files()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in ProductionFiles())
            if (GuardedKeyReadCount(File.ReadAllLines(f)) > 0) found.Add(Path.GetFileName(f));

        var unexpected = found.Except(KnownDevFiles).ToList();
        var missing = KnownDevFiles.Except(found).ToList();
        Assert.True(unexpected.Count == 0,
            "New LWDEV files carry a key read; add the entry to KnownDevFiles: " + string.Join(", ", unexpected));
        Assert.True(missing.Count == 0,
            "These files no longer carry a fenced key read (or the scan is blind); remove the entry: " +
            string.Join(", ", missing));
    }

    private static int CountBytes(byte[] hay, byte[] needle)
    {
        int count = 0;
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) count++;
        }
        return count;
    }

    [Fact]
    public void The_referenced_runtime_binary_carries_no_key_read_import()
    {
        byte[] dll = File.ReadAllBytes(typeof(LivingWeapon.Mod).Assembly.Location);
        Assert.True(CountBytes(dll, Encoding.UTF8.GetBytes("MessageBoxW")) >= 1,
            "positive control missing, the binary was not read correctly");
        int hits = CountBytes(dll, Encoding.UTF8.GetBytes("GetAsyncKeyState")) +
                   CountBytes(dll, Encoding.Unicode.GetBytes("GetAsyncKeyState"));
#if LWDEV
        Assert.True(hits >= 1, "a dev compile must carry the import, the scanner is blind");
#else
        Assert.True(hits == 0, "a release compile carries a key read import; a player could press it");
#endif
    }
}
