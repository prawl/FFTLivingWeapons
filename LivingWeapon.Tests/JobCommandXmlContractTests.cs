using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-50 review should-fix: pins that our OWN mod/FFTIVC/tables/enhanced/JobCommandData.xml never
/// overrides the ability bytes at JobCommand rec 8 (Archer, Aim) or rec 9 (Monk, Martial Arts):
/// the two records LaunchGuard's JobCommand landmark signature-checks (Barrage.cs:31-43,
/// tools/probes/jobcommand_find_probe.py:44-45). A modloader table merge is cell-level, so if this
/// mod ever rebalanced those records' ability lists itself, the baked signature would go stale on
/// our OWN data, not just a hypothetical rival mod, and the guard would falsely stand down on
/// every launch. This test is the tripwire: touching those records here must re-derive the
/// signature (or drop the overridden record) in the same change, not silently break launch.
/// </summary>
public class JobCommandXmlContractTests
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
    public void Rec8_and_rec9_never_declare_an_AbilityId_field()
    {
        string path = Path.Combine(RepoRoot(), "mod", "FFTIVC", "tables", "enhanced", "JobCommandData.xml");
        Assert.True(File.Exists(path), $"{path} does not exist");

        var doc = XDocument.Load(path);
        var offenders = doc.Descendants("JobCommand")
            .Where(jc => int.TryParse((string?)jc.Element("Id"), out int id) && (id == 8 || id == 9))
            .Where(jc => jc.Elements().Any(e => e.Name.LocalName.StartsWith("AbilityId", StringComparison.Ordinal)))
            .Select(jc => (string?)jc.Element("Id"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "JobCommand record(s) " + string.Join(", ", offenders) +
            " declare an AbilityId field: re-derive the JobCommand landmark bytes or drop the overridden record");
    }
}
