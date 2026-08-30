using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-106: the proof-claim ratchet. This repo's rule is that "proven live" is a ledger fact,
/// not a doc-comment vibe: docs/LIVE_LEDGER.md is the only authority on whether a runtime
/// mechanism claim is proven, and only the owner moves a row into its Proven section. The rule
/// was written down and broken anyway. LW-105 found NINE sentences across code, tests and the
/// changelog asserting live proof for a row that sat under Uncertain, and only two of the nine
/// were known in advance; a five-surface sweep found the rest. A rule a grep cannot enforce
/// decays, so this is the machine behind it.
///
/// WHAT IT DOES: counts proof-claim phrases per file and compares against a frozen baseline.
/// Any change fails, so adding a claim forces the author to look at LIVE_LEDGER and then
/// deliberately bump a number. The failure message prints the ready-to-paste replacement.
///
/// WHY A COUNT AND NOT A ROW MAPPING: the original LW-106 sketch was an allow-list naming the
/// ledger row behind every claim. The seeding grep killed it: 197 claims across 65 files is a
/// 197-row hand mapping that churns on every reword. The count ratchet is ~50x cheaper to seed,
/// churns only when a claim is added or removed, and still catches every addition (LW-90's four
/// new claims landed in ONE commit, which this would have stopped).
///
/// WHAT THE BASELINE DOES NOT MEAN: these numbers record that the claims EXISTED on 2026-07-21.
/// They do NOT assert each one traces to a row in the Proven section; auditing the pre-existing
/// 197 is separate work (see the backlog). Treat a baseline number as debt, not as a warranty.
///
/// KNOWN GAP, accepted deliberately: deleting one claim and adding a different one in the SAME
/// file leaves the count equal and passes. Closing it needs per-line text freezing, which is the
/// churn this design exists to avoid.
/// </summary>
public class ProofClaimContractTests
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

    /// <summary>The phrases that assert live proof. Deliberately narrow: each one states that
    /// something WAS PROVEN in the running game, which is precisely the assertion only a Proven
    /// ledger row may carry. Hedged vocabulary ("observed", "premise", "working theory",
    /// "candidate", "suspected") is what an unproven mechanism is supposed to say, so it is not
    /// matched. Case-insensitive, which folds "PROVEN LIVE" into "proven live" and "LIVE-PROVEN"
    /// into "live-proven".</summary>
    private static readonly Regex ClaimRegex = new(
        @"live-proven|proven live|live-verified|verified live|owner-proven|proving the premise",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>This file is excluded from its own scan: it necessarily contains every phrase it
    /// hunts for (in the regex above and the Theory cases below), the same reason the attribution
    /// pre-commit hook lives outside the tree it scans.</summary>
    private const string SelfExclusion = "ProofClaimContractTests.cs";

    /// <summary>Scanned surfaces: production code, tests, and the top-level CONTRACT docs.
    /// EXCLUDED: docs/LIVE_LEDGER.md (it IS the authority, and its Proven section is supposed to
    /// say "proven"), and docs/research + docs/archive (JOURNAL and ARCHIVED tier, which are
    /// historical records allowed to preserve what was believed at the time). Source enumeration
    /// is RECURSIVE (the projects folder their files by domain) with obj/bin pruned; the KEY
    /// stays the project-qualified FILENAME ("LivingWeapon/Bulwark.cs" wherever the file sits)
    /// so the frozen baseline below survives folder moves. That scheme would silently merge two
    /// same-named files, so Scan_covers_the_expected_surfaces asserts key uniqueness.</summary>
    private static IEnumerable<(string Key, string FullPath)> ScannedFiles(string root)
    {
        foreach (var dir in new[] { "LivingWeapon", "LivingWeapon.Tests" })
            foreach (var f in Directory.GetFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories)
                         .Where(f => !HasDirSegment(f, "obj") && !HasDirSegment(f, "bin")).OrderBy(f => f))
                if (Path.GetFileName(f) != SelfExclusion)
                    yield return ($"{dir}/{Path.GetFileName(f)}", f);

        foreach (var f in Directory.GetFiles(Path.Combine(root, "docs"), "*.md").OrderBy(f => f))
            if (Path.GetFileName(f) != "LIVE_LEDGER.md")
                yield return ($"docs/{Path.GetFileName(f)}", f);
    }

    /// <summary>Segment-aware directory test (DocsContractTests.HasDirSegment's twin): a bare
    /// Contains would also match a folder merely ENDING in "obj".</summary>
    private static bool HasDirSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, int> CurrentCounts(string root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, fullPath) in ScannedFiles(root))
        {
            int n = ClaimRegex.Matches(File.ReadAllText(fullPath)).Count;
            if (n > 0) counts[key] = n;
        }
        return counts;
    }

    /// <summary>Frozen 2026-07-21 (LW-106). To change a number here you must first check
    /// docs/LIVE_LEDGER.md: a claim is only allowed to say "proven" if its row really sits in
    /// the Proven section, and only the owner puts it there.</summary>
    private static readonly Dictionary<string, int> Baseline = new(StringComparer.Ordinal)
    {
        { "LivingWeapon/ActorResolver.Flags.cs", 1 },
        { "LivingWeapon/AttackCard.cs", 1 },
        { "LivingWeapon/AttackCardProbeText.cs", 1 },
        { "LivingWeapon/AttackRow.Policy.cs", 2 },
        { "LivingWeapon/AttackRow.cs", 1 },
        { "LivingWeapon/Band.cs", 4 },
        { "LivingWeapon/Barrage.Policy.cs", 3 },
        { "LivingWeapon/BodyDoubleSpike.cs", 13 },
        // Bulwark's terrain-veto rework (2026-07-28): the class docs cite the LIVE_LEDGER
        // Contradicted-section terrain entry's SETTLED sub-facts (grid base correction, the byte
        // +6 bit 0x02 obstacle mechanism, the facing byte) -- owner-verified live content, just
        // recorded as an in-place correction on that row rather than moved to a fresh Proven row.
        { "LivingWeapon/Bulwark.Policy.cs", 1 },
        { "LivingWeapon/Bulwark.cs", 1 },
        { "LivingWeapon/CtTurns.cs", 1 },
        { "LivingWeapon/EagleEye.cs", 1 },
        { "LivingWeapon/Engine.cs", 1 },
        { "LivingWeapon/ExtraTurn.Policy.cs", 2 },
        { "LivingWeapon/ExtraTurn.cs", 2 },
        { "LivingWeapon/GunSlinger.cs", 1 },
        { "LivingWeapon/Iai.cs", 2 },
        { "LivingWeapon/KillTracker.Delayed.cs", 2 },
        { "LivingWeapon/Kobu.cs", 2 },
        { "LivingWeapon/LaunchGuard.Landmarks.cs", 1 },
        { "LivingWeapon/Maim.Policy.cs", 1 },
        { "LivingWeapon/Maim.cs", 1 },
        // 11 -> 12 (2026-07-28): the Bulwark terrain-grid block gained one more "live-proven"
        // citation for the ALayerBit facing-bits doc, same ledger row as the Bulwark.cs bump above.
        { "LivingWeapon/Offsets.cs", 12 },
        { "LivingWeapon/Plague.cs", 1 },
        { "LivingWeapon/Rapture.cs", 1 },
        { "LivingWeapon/ShadowBlade.cs", 1 },
        { "LivingWeapon/Signatures.cs", 1 },
        { "LivingWeapon/SpiritualFont.Policy.cs", 1 },
        { "LivingWeapon/SpiritualFont.cs", 2 },
        { "LivingWeapon/Tuning.cs", 4 },
        { "LivingWeapon/TurnOwnerProbe.cs", 1 },
        { "LivingWeapon/TurnTracker.cs", 4 },
        { "LivingWeapon/WeaponMeta.cs", 1 },
        { "LivingWeapon.Tests/AttackRowPolicyTests.cs", 1 },
        { "LivingWeapon.Tests/BandTests.cs", 1 },
        { "LivingWeapon.Tests/BarrageTests.cs", 9 },
        { "LivingWeapon.Tests/BattleStateTests.cs", 1 },
        { "LivingWeapon.Tests/CavalierChargeTests.cs", 1 },
        { "LivingWeapon.Tests/CrossTurnSummonTests.cs", 1 },
        { "LivingWeapon.Tests/DelayedActorTests.cs", 1 },
        { "LivingWeapon.Tests/FlagOwnerResolveTests.cs", 1 },
        { "LivingWeapon.Tests/JobCommandXmlContractTests.cs", 1 },
        { "LivingWeapon.Tests/JobDataXmlContractTests.cs", 1 },
        { "LivingWeapon.Tests/KobuTests.cs", 1 },
        { "LivingWeapon.Tests/RaptureTests.cs", 1 },
        { "LivingWeapon.Tests/RaptureWindowTests.cs", 2 },
        { "LivingWeapon.Tests/SpiritualFontTests.cs", 1 },
        // New (2026-07-28, Bulwark's back-tile rework): "live-proven" (the facing byte) and
        // "proven live" (the grid writes persisting the whole process session) both cite the
        // LIVE_LEDGER Contradicted-section terrain entry's settled sub-facts -- real owner-
        // verified content, same justification as the Bulwark.cs/Bulwark.Policy.cs bump above.
        { "docs/BULWARK_AC.md", 2 },
        // 25 -> 26 (2026-07-27, LW-127 exit row): "Owner verified live the same day" -- the claim is
        // the owner's own in-session sign-off on the LW-127 live passes (two clean casts plus the
        // three-cast tape), given before the exit row was written. It asserts the FEATURE's owner
        // verdict, not a LIVE_LEDGER mechanism row; the supporting turn-order rows deliberately
        // stay under Uncertain until the owner flips them, and the row says so.
        // 26 -> 27 (2026-08-17, LW-252 exit row): "Owner live-verified the same day" in the
        // SHIPPED 3b6786f entry. Backed by the ledger: [party-nameid-unique-key] sits in the
        // Proven section, owner-flipped d038fa7 with the twin-chocobo trial evidence in its fold.
        { "docs/CHANGELOG.md", 27 },
        { "docs/DESIGN.md", 1 },
        { "docs/DEV_TEST_RECIPES.md", 2 },
        { "docs/MECHANICS.md", 20 },
        { "docs/RELEASE_SCOPE.md", 7 },
        // 8 -> 7 (2026-07-25, LW-131 fixed): the deleted Backlog block's drop was never really
        // about an argument the count was tracking. The regex only matched an INCIDENTAL substring:
        // the phrase "Proven LIVE_LEDGER row this code cites" in that block matched "proven live"
        // across the word boundary in "Proven LIVE_LEDGER". Deleting the block removed that
        // accidental hit, not a reworded claim.
        // 7 -> 8 (2026-08-30, LW-358 promoted): the new Now row cites the basic-Attack
        // discriminator premise as "live-proven 2026-08-12"; that leans on LIVE_LEDGER's
        // [basic-attack-discriminator] row, which sits in the Proven section (owner-flipped
        // 2026-08-12 on the LW-167 arming pass), so the claim is real and the count moves.
        { "docs/TODO.md", 8 },
        { "docs/VERIFY_LIVE.md", 3 },
    };

    // --- The phrase set itself, pinned so a future edit cannot quietly narrow the net ---

    [Theory]
    [InlineData("the mechanism is live-proven 2026-07-21", true)]
    [InlineData("PROVEN LIVE by the owner's repro session", true)]
    [InlineData("this was proven live on tape", true)]
    [InlineData("live-verified the same day", true)]
    [InlineData("owner-proven on the Padded Vest", true)]
    [InlineData("x2), proving the premise. The corrective must", true)]
    [InlineData("observed live 2026-07-21, ledger row still Uncertain", false)]
    [InlineData("the working premise, never isolated", false)]
    [InlineData("candidate mechanism, suspected but unverified", false)]
    public void ClaimRegex_matches_proof_assertions_and_not_hedged_wording(string line, bool expected)
        => Assert.Equal(expected, ClaimRegex.IsMatch(line));

    // --- The ratchet ---

    [Fact]
    public void Proof_claim_counts_match_the_frozen_baseline()
    {
        string root = RepoRoot();
        var current = CurrentCounts(root);

        var drift = new List<string>();
        foreach (var file in current.Keys.Union(Baseline.Keys).OrderBy(f => f, StringComparer.Ordinal))
        {
            Baseline.TryGetValue(file, out int was);
            current.TryGetValue(file, out int now);
            if (was != now) drift.Add($"  {{ \"{file}\", {now} }},   // baseline said {was}");
        }

        Assert.True(drift.Count == 0,
            "Proof-claim counts drifted from the LW-106 baseline.\n\n" +
            "If you ADDED a claim: open docs/LIVE_LEDGER.md and confirm the row you are leaning on\n" +
            "really sits in its Proven section. Only the owner puts a row there. If it does not,\n" +
            "reword the claim to what is true (observed / working premise / ledger row Uncertain)\n" +
            "rather than bumping the number. If it does, bump the number and say so in the commit.\n" +
            "If you REMOVED a claim, lower the number.\n\n" +
            "Ready-to-paste baseline rows for the files that moved:\n" +
            string.Join("\n", drift));
    }

    /// <summary>The scan must actually reach the files it claims to cover: an empty or tiny
    /// sweep would make the ratchet above pass vacuously forever.</summary>
    [Fact]
    public void Scan_covers_the_expected_surfaces()
    {
        var files = ScannedFiles(RepoRoot()).Select(f => f.Key).ToList();
        Assert.True(files.Count(f => f.StartsWith("LivingWeapon/")) > 50, $"production sweep too small: {files.Count}");
        Assert.True(files.Count(f => f.StartsWith("LivingWeapon.Tests/")) > 20, "test sweep too small");
        Assert.Contains("docs/CHANGELOG.md", files);
        Assert.DoesNotContain("docs/LIVE_LEDGER.md", files);
        Assert.DoesNotContain($"LivingWeapon.Tests/{SelfExclusion}", files);
        var dupes = files.GroupBy(f => f, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0,
            "Two scanned files share a project-qualified filename, which would merge their baseline rows:\n" +
            string.Join("\n", dupes));
    }
}
