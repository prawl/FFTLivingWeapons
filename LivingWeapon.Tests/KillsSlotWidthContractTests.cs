using System;
using System.IO;
using System.Text.RegularExpressions;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-148: tools/lib/flavor.py's KILLS_SLOT_BODY_CHARS and LivingWeapon/Signatures/Signatures.cs's
/// KillsMeterSlotChars are one width pinned in two languages on purpose (Signatures.cs's own doc
/// comment says so, and flavor.py's says it mirrors Signatures byte-for-byte): the Python bake
/// pads every living weapon's "Kills: " scaffold line to this many characters, and the C# card
/// painter (ByteScan.MeterSlotDigits via CardScanner/CardSites) repaints exactly that many bytes
/// back in place at runtime. analyze.py's own check_kills_scaffold_lockstep only checks the
/// PYTHON SIDE against this number -- it never reads the C# constant, so the two comments claimed
/// a pin that no test actually crossed languages to enforce. This test does: regex-extract the
/// Python constant's literal value out of tools/lib/flavor.py's file text (the
/// PipelineManifestContractTests.cs pattern) and assert it against the compiled C# constant.
/// </summary>
public class KillsSlotWidthContractTests
{
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

    [Fact]
    public void Python_KILLS_SLOT_BODY_CHARS_matches_Signatures_KillsMeterSlotChars()
    {
        string flavorPath = Path.Combine(RepoRoot(), "tools", "lib", "flavor.py");
        string text = File.ReadAllText(flavorPath);

        var match = Regex.Match(text, @"^KILLS_SLOT_BODY_CHARS\s*=\s*(\d+)\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, "KILLS_SLOT_BODY_CHARS = <int> not found in tools/lib/flavor.py "
            + "(vacuous-pass guard: this test never silently passes without actually reading the constant)");

        int pythonValue = int.Parse(match.Groups[1].Value);

        Assert.Equal(Signatures.KillsMeterSlotChars, pythonValue);
    }

    /// <summary>LW-311: the BAKED scaffold body must pass the C# site-admission validator, or the
    /// painter can never find an unpainted card and the counter silently dies for everyone. This
    /// is the drift the dash rebake makes possible (a Python-side scaffold tweak with no C#-side
    /// validator move), so the pin crosses languages the same way the width pin above does.</summary>
    [Fact]
    public void Python_KILLS_SCAFFOLD_body_passes_the_CSharp_meter_slot_validator()
    {
        string flavorPath = Path.Combine(RepoRoot(), "tools", "lib", "flavor.py");
        string text = File.ReadAllText(flavorPath);

        var match = Regex.Match(text, "^KILLS_SCAFFOLD = \"(.*)\"\\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, "KILLS_SCAFFOLD = \"...\" not found in tools/lib/flavor.py "
            + "(vacuous-pass guard: this test never silently passes without reading the constant)");

        string scaffold = match.Groups[1].Value;
        Assert.StartsWith("Kills: ", scaffold);
        string body = scaffold.Substring("Kills: ".Length);
        Assert.Equal(Signatures.KillsMeterSlotChars, body.Length);

        foreach (int enc in new[] { 1, 2 })
        {
            byte[] buf = ByteScan.Enc(body, enc);
            Assert.True(ByteScan.MeterSlotDigits(buf, 0, enc, Signatures.KillsMeterSlotChars),
                $"baked scaffold body '{body}' must pass the site-admission validator (enc {enc})");
        }
    }
}
