using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-77: mod/FFTIVC/tables/enhanced/JobData.xml is a WHOLE-ROW writeback at OnAllModsLoaded
/// (FFTOJobDataManager.ApplyTablePatch, model.X ?? previous.X across every field, incl.
/// JobCommandId since loader 1.7.1), so any row this table lists clobbers another job mod's
/// post-snapshot runtime edits to that SAME row, proven live 2026-07-14 (Blue And Red Mages
/// 2.0.2: deleting our row 57 resurrected Red Mage). The fix is to list only rows carrying a
/// real payload. This is the LOAD-BEARING test: it pins BOTH directions at once, over-emission
/// (a row creeping back in that carries no real payload, widening the collision surface again)
/// and under-emission (a payload row silently dropped, losing real gameplay behavior).
///
/// The 28-id keep-set: tools/make_jobequip.py's CEV_ALLOW ({1,2,3,4,5,6,7,15,145} plus every
/// generic human non-Lucavi id 61-93) union every id whose equip list carries a real addition or
/// strip today. Of the low story ids, only 3 (Gallant Knight, a real Knife strip), 4 (a C-EV
/// floor raise), and 15 (Dragonkin, a C-EV floor raise) actually emit; the rest of CEV_ALLOW sits
/// at or above the floor already (harmless future-proofing). Job 145 (Automaton/Construct 8) is
/// player-recruitable (Puppeteer.Policy.cs:33, PuppeteerTests.cs:35), so it keeps its C-EV floor
/// even though it carries no equip payload.
/// </summary>
public class JobDataXmlContractTests
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

    /// <summary>The 28-id keep set. Derivation: tools/make_jobequip.py CEV_ALLOW ({1,2,3,4,5,6,7,
    /// 15,145}) union every generic human non-Lucavi id (61-93), filtered to ids that actually
    /// emit a payload against today's TABLE_DATA vanilla (reviewer-derived and re-confirmed for
    /// plan v2, 2026-07-14).</summary>
    private static readonly HashSet<int> ExpectedJobIds = new()
    {
        3, 4, 15, 63, 66, 68, 70, 71, 74, 75, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
        90, 91, 92, 93, 145,
    };

    private static List<XElement> LoadJobs()
    {
        string path = Path.Combine(RepoRoot(), "mod", "FFTIVC", "tables", "enhanced", "JobData.xml");
        Assert.True(File.Exists(path), $"{path} does not exist");
        var doc = XDocument.Load(path);
        return doc.Descendants("Job").ToList();
    }

    [Fact]
    public void Emitted_job_ids_equal_the_LW77_keep_set()
    {
        var jobs = LoadJobs();
        var actualIds = jobs.Select(j => int.Parse((string)j.Element("Id")!)).ToHashSet();

        var missing = ExpectedJobIds.Except(actualIds).OrderBy(i => i).ToList();
        var extra = actualIds.Except(ExpectedJobIds).OrderBy(i => i).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            "JobData.xml's emitted id set drifted from the LW-77 keep set. " +
            $"Missing (payload lost): [{string.Join(", ", missing)}]. " +
            $"Extra (collision surface widened again): [{string.Join(", ", extra)}].");
    }

    [Fact]
    public void Every_emitted_job_carries_at_least_one_payload_element()
    {
        var jobs = LoadJobs();
        Assert.NotEmpty(jobs);

        var payloadless = new List<string>();
        foreach (var job in jobs)
        {
            string id = (string)job.Element("Id")!;
            bool hasPayload = job.Elements().Any(e =>
                e.Name.LocalName == "EquippableItems" ||
                e.Name.LocalName == "CharacterEvasion" ||
                e.Name.LocalName.StartsWith("InnateAbilityId", StringComparison.Ordinal));
            if (!hasPayload) payloadless.Add(id);
        }

        Assert.True(payloadless.Count == 0,
            "Job row(s) with no payload element (a whole-row writeback with nothing to justify it): " +
            string.Join(", ", payloadless));
    }
}
