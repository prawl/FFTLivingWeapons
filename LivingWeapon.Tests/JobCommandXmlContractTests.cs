using System;
using System.IO;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-77: mod/FFTIVC/tables/enhanced/JobCommandData.xml is DELETED. A JobCommandData row is the
/// same whole-row-writeback hazard as JobData.xml (every AbilityId/RSM field via
/// model.X ?? previous.X at OnAllModsLoaded, proven live 2026-07-14 on JobData.xml), and this
/// table's own record list happened to include rec 8 (Archer, Aim) and rec 9 (Monk, Martial
/// Arts): the two records LaunchGuard's JobCommand landmark signature-checks (Barrage.cs:31-43,
/// tools/probes/jobcommand_find_probe.py:44-45). The table's sole payload (zeroing the dead-JP
/// Equip Axes RSM slot, ability id 460, across 47 records) is replaced by one ability.en.nxd
/// Description cell on key 460 (tools/patch_ability_names.py), the proven per-cell merge lane.
///
/// This test is the tripwire's other half: the file must not exist at all. If a future change
/// ever reintroduces mod JobCommandData.xml, it must either avoid recs 8 and 9 entirely or
/// re-derive the LaunchGuard signature in the same change, not silently break launch.
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
    public void Mod_JobCommandData_xml_does_not_exist()
    {
        string path = Path.Combine(RepoRoot(), "mod", "FFTIVC", "tables", "enhanced", "JobCommandData.xml");
        Assert.False(File.Exists(path),
            $"{path} exists: JobCommandData.xml was deleted for LW-77 (whole-row writeback collision); " +
            "re-derive the LaunchGuard rec8/rec9 signature if this table ever comes back, or avoid those records.");
    }
}
