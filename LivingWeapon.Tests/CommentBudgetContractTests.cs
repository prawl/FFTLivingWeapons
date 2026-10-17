using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// The comment budget. The runtime once carried four comment lines for every five code lines,
/// essays written to persuade a reviewer, and the owner asked for comments that read like an
/// engineer left them (2026-10-16). This test holds that line mechanically.
///
/// Rules, per production file (LivingWeapon/, recursive, obj and bin pruned).
///   1. A file in the frozen baseline may not grow its comment lines past baseline plus ten
///      percent (minimum three lines), and its longest run of consecutive comment lines may not
///      grow at all.
///   2. A file not in the baseline (new) keeps comment lines at or under a third of its code lines
///      and no comment block longer than eight lines.
///   3. No comment anywhere carries a tell phrase from <see cref="TellRegex"/>.
///   4. No comment carries a colon or a dash inside a sentence; the owner never writes either.
///      Quoted strings, xml tags, hex, clock times, file and line cites and list bullets are skipped.
/// A red prints ready to paste baseline rows. Shrinking a number is always allowed; only the
/// owner raises one. Keys are project qualified filenames so folder moves do not churn the table.
/// </summary>
public class CommentBudgetContractTests
{
    private const double NewFileMaxRatio = 1.0 / 3.0;
    private const int NewFileMaxBlock = 8;
    private const double GrowthAllowance = 0.10;
    private const int GrowthFloor = 3;

    /// <summary>Phrases that mark a comment as written to persuade rather than to inform.</summary>
    private static readonly Regex TellRegex = new(
        @"load-bearing|deliberately|by design|review blocker|verifier NIT|owner ruling|owner direction|precedent|doctrine",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        throw new FileNotFoundException("repo root not found above the test bin dir");
    }

    private static bool HasDirSegment(string path, string segment)
        => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
               .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Key, string FullPath)> ProductionFiles(string root)
    {
        foreach (var f in Directory.GetFiles(Path.Combine(root, "LivingWeapon"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !HasDirSegment(f, "obj") && !HasDirSegment(f, "bin")).OrderBy(f => f))
            yield return ($"LivingWeapon/{Path.GetFileName(f)}", f);
    }

    internal readonly record struct Measure(int CommentLines, int CodeLines, int MaxBlock, int Colons, int Dashes, List<string> Tells);

    /// <summary>A comment line starts with //, ///, /* or *. A trailing comment after code counts
    /// toward tells, colons and dashes but not toward the line budget.</summary>
    internal static Measure MeasureText(string text)
    {
        int comment = 0, code = 0, run = 0, maxRun = 0, colons = 0, dashes = 0;
        var tells = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string s = raw.Trim();
            if (s.Length == 0) continue;
            string? commentText = null;
            if (s.StartsWith("//") || s.StartsWith("/*") || s.StartsWith("*"))
            {
                comment++; run++; maxRun = Math.Max(maxRun, run); commentText = s;
            }
            else
            {
                code++; run = 0;
                int k = raw.IndexOf(" //", StringComparison.Ordinal);
                if (k >= 0) commentText = raw[k..];
            }
            if (commentText is null) continue;
            string body = Regex.Replace(commentText, @"<[^>]+>|""[^""]*""|`[^`]*`", "#");     // xml tags, quoted strings, code spans
            body = Regex.Replace(body, @"\b0x[0-9A-Fa-f]+\b|\b\d+:\d+\b|\b[\w.]+\.(cs|py|md|ps1):\d+", "#");   // hex, clock times, file and line cites
            string prose = Regex.Replace(body, @"^(?:///|//|/\*|\*)\s*-\s", "");            // a list bullet is not a dash in a sentence
            colons += Regex.Matches(body, @"\w: ").Count;
            dashes += Regex.Matches(prose, @"(?<=[A-Za-z0-9)]) - (?=[A-Za-z0-9(])|—").Count;
            foreach (Match m in TellRegex.Matches(body)) tells.Add(m.Value);
        }
        return new Measure(comment, code, maxRun, colons, dashes, tells);
    }

    private static Dictionary<string, Measure> Current(string root)
        => ProductionFiles(root).ToDictionary(p => p.Key, p => MeasureText(File.ReadAllText(p.FullPath)), StringComparer.Ordinal);

    /// <summary>Frozen after the 2026-10-16 sweep, comment lines and longest block per file.
    /// Shrink freely; raise only with the owner.</summary>
    private static readonly Dictionary<string, (int Lines, int Block)> Baseline = new(StringComparer.Ordinal)
    {
        { "LivingWeapon/AccessorEntryHook.cs", (21, 14) },
        { "LivingWeapon/ActedPeriodLatch.cs", (42, 7) },
        { "LivingWeapon/ActivationEdge.cs", (5, 4) },
        { "LivingWeapon/ActorRegister.Bridge.cs", (27, 8) },
        { "LivingWeapon/ActorRegister.cs", (82, 26) },
        { "LivingWeapon/ActorResolver.Cursor.cs", (32, 14) },
        { "LivingWeapon/ActorResolver.Flags.cs", (32, 19) },
        { "LivingWeapon/ActorResolver.TurnQueue.cs", (30, 9) },
        { "LivingWeapon/ActorResolver.cs", (58, 10) },
        { "LivingWeapon/Afterimage.Policy.cs", (15, 4) },
        { "LivingWeapon/AnchorScan.cs", (24, 7) },
        { "LivingWeapon/AnchorScout.Report.cs", (12, 6) },
        { "LivingWeapon/AnchorScout.cs", (38, 15) },
        { "LivingWeapon/AssemblyInfo.cs", (1, 1) },
        { "LivingWeapon/AttackCard.Census.cs", (22, 5) },
        { "LivingWeapon/AttackCard.Enc2.cs", (9, 5) },
        { "LivingWeapon/AttackCard.Paint.cs", (26, 5) },
        { "LivingWeapon/AttackCard.Resolve.cs", (27, 7) },
        { "LivingWeapon/AttackCard.cs", (58, 38) },
        { "LivingWeapon/AttackCardProbeText.cs", (37, 8) },
        { "LivingWeapon/AttackCardTail.cs", (16, 8) },
        { "LivingWeapon/AttackCardText.cs", (10, 6) },
        { "LivingWeapon/AttackRow.Policy.cs", (41, 8) },
        { "LivingWeapon/AttackRow.cs", (17, 5) },
        { "LivingWeapon/BagReplay.cs", (27, 11) },
        { "LivingWeapon/Band.Sanity.cs", (17, 8) },
        { "LivingWeapon/Band.cs", (40, 9) },
        { "LivingWeapon/BandHeal.cs", (10, 3) },
        { "LivingWeapon/BandSweep.cs", (11, 5) },
        { "LivingWeapon/BannerToast.Policy.cs", (27, 7) },
        { "LivingWeapon/BannerToast.cs", (33, 8) },
        { "LivingWeapon/Barrage.Policy.cs", (64, 16) },
        { "LivingWeapon/Barrage.State.cs", (9, 4) },
        { "LivingWeapon/Barrage.cs", (34, 9) },
        { "LivingWeapon/BattleCensus.cs", (21, 10) },
        { "LivingWeapon/BattleLog.cs", (11, 7) },
        { "LivingWeapon/BattleState.cs", (63, 11) },
        { "LivingWeapon/BattleSummary.cs", (11, 7) },
        { "LivingWeapon/Benediction.Policy.cs", (17, 11) },
        { "LivingWeapon/Benediction.cs", (22, 13) },
        { "LivingWeapon/BodyDoubleSpike.cs", (250, 80) },
        { "LivingWeapon/Bulwark.Policy.cs", (33, 14) },
        { "LivingWeapon/Bulwark.Terrain.cs", (26, 8) },
        { "LivingWeapon/Bulwark.cs", (33, 21) },
        { "LivingWeapon/BytePatchSet.cs", (14, 5) },
        { "LivingWeapon/ByteScan.cs", (15, 5) },
        { "LivingWeapon/CardLine.cs", (26, 15) },
        { "LivingWeapon/CardPatterns.cs", (34, 5) },
        { "LivingWeapon/CardScanner.Anchors.cs", (16, 5) },
        { "LivingWeapon/CardScanner.cs", (35, 12) },
        { "LivingWeapon/CardSites.Admission.cs", (51, 11) },
        { "LivingWeapon/CardSites.Reliquary.cs", (8, 5) },
        { "LivingWeapon/CardSites.Verify.cs", (32, 10) },
        { "LivingWeapon/CardSites.cs", (33, 6) },
        { "LivingWeapon/CardVerdict.cs", (31, 13) },
        { "LivingWeapon/CategoryGetterHook.cs", (12, 8) },
        { "LivingWeapon/Choir.Policy.cs", (6, 4) },
        { "LivingWeapon/Choir.cs", (17, 8) },
        { "LivingWeapon/ChunkReader.cs", (20, 5) },
        { "LivingWeapon/CodePatcher.cs", (14, 10) },
        { "LivingWeapon/Config.cs", (5, 5) },
        { "LivingWeapon/Configurable.cs", (7, 3) },
        { "LivingWeapon/Configurator.cs", (8, 3) },
        { "LivingWeapon/ConfiguratorMixinBase.cs", (2, 1) },
        { "LivingWeapon/CorpseDespawn.Steps.cs", (16, 4) },
        { "LivingWeapon/CorpseDespawn.cs", (32, 19) },
        { "LivingWeapon/CreditGate.cs", (8, 6) },
        { "LivingWeapon/CtTurns.cs", (9, 7) },
        { "LivingWeapon/CursorGate.cs", (25, 10) },
        { "LivingWeapon/DeedFanout.cs", (5, 5) },
        { "LivingWeapon/Display.Flight.cs", (51, 10) },
        { "LivingWeapon/Display.Heartbeat.cs", (37, 14) },
        { "LivingWeapon/Display.PoolDrain.cs", (38, 20) },
        { "LivingWeapon/Display.PoolLocate.cs", (10, 7) },
        { "LivingWeapon/Display.PoolPaint.cs", (65, 24) },
        { "LivingWeapon/Display.PoolPaintCadence.cs", (19, 6) },
        { "LivingWeapon/Display.PoolPaintLog.cs", (19, 9) },
        { "LivingWeapon/Display.cs", (80, 9) },
        { "LivingWeapon/DisplaySweep.cs", (22, 9) },
        { "LivingWeapon/DrillTrigger.cs", (12, 5) },
        { "LivingWeapon/EagleEye.Policy.cs", (7, 3) },
        { "LivingWeapon/EagleEye.cs", (26, 13) },
        { "LivingWeapon/EarnedAnchors.cs", (26, 8) },
        { "LivingWeapon/EnemyFingerprintCache.cs", (7, 6) },
        { "LivingWeapon/EnemyOracle.cs", (46, 16) },
        { "LivingWeapon/Engine.cs", (182, 13) },
        { "LivingWeapon/ExtendedBagSidecar.Format.cs", (1, 1) },
        { "LivingWeapon/ExtendedBagSidecar.cs", (27, 17) },
        { "LivingWeapon/ExtendedCatalog.cs", (17, 9) },
        { "LivingWeapon/ExtendedDonorThunks.cs", (15, 8) },
        { "LivingWeapon/ExtendedInventory.Arm.cs", (16, 7) },
        { "LivingWeapon/ExtendedInventory.Hooks.cs", (14, 6) },
        { "LivingWeapon/ExtendedInventory.Tick.cs", (31, 10) },
        { "LivingWeapon/ExtendedInventory.cs", (21, 9) },
        { "LivingWeapon/ExtendedInventoryData.cs", (16, 5) },
        { "LivingWeapon/ExtendedRecords.cs", (19, 8) },
        { "LivingWeapon/ExtendedSites.cs", (92, 21) },
        { "LivingWeapon/ExtraTurn.Policy.cs", (25, 7) },
        { "LivingWeapon/ExtraTurn.cs", (15, 10) },
        { "LivingWeapon/FeignDeath.Policy.cs", (49, 6) },
        { "LivingWeapon/FeignDeath.cs", (30, 10) },
        { "LivingWeapon/FileConsoleLogger.cs", (23, 8) },
        { "LivingWeapon/FingerprintGuard.cs", (39, 9) },
        { "LivingWeapon/FlavorStamp.cs", (12, 6) },
        { "LivingWeapon/Flight.cs", (19, 8) },
        { "LivingWeapon/FlightRecorder.Policy.cs", (9, 4) },
        { "LivingWeapon/FlightRecorder.cs", (30, 9) },
        { "LivingWeapon/GameMemory.cs", (11, 3) },
        { "LivingWeapon/Grid.cs", (6, 5) },
        { "LivingWeapon/GrowthEngine.Afterimage.cs", (16, 8) },
        { "LivingWeapon/GrowthEngine.Lanes.cs", (66, 10) },
        { "LivingWeapon/GrowthEngine.Locate.cs", (39, 6) },
        { "LivingWeapon/GrowthEngine.Mushin.cs", (14, 8) },
        { "LivingWeapon/GrowthEngine.Signatures.cs", (42, 8) },
        { "LivingWeapon/GrowthEngine.TimedStat.cs", (30, 12) },
        { "LivingWeapon/GrowthEngine.Ultima.cs", (15, 8) },
        { "LivingWeapon/GrowthEngine.cs", (35, 9) },
        { "LivingWeapon/GunSlinger.Apply.cs", (10, 4) },
        { "LivingWeapon/GunSlinger.MenuGate.cs", (28, 10) },
        { "LivingWeapon/GunSlinger.Policy.cs", (65, 13) },
        { "LivingWeapon/GunSlinger.Reconcile.cs", (18, 8) },
        { "LivingWeapon/GunSlinger.Store.cs", (14, 5) },
        { "LivingWeapon/GunSlinger.cs", (43, 21) },
        { "LivingWeapon/HealPulse.cs", (15, 8) },
        { "LivingWeapon/HookLandmark.cs", (8, 8) },
        { "LivingWeapon/HotChunkSet.cs", (19, 4) },
        { "LivingWeapon/HpDeltaState.cs", (15, 6) },
        { "LivingWeapon/ILogger.cs", (18, 6) },
        { "LivingWeapon/ISignature.cs", (14, 5) },
        { "LivingWeapon/Iai.Policy.cs", (92, 29) },
        { "LivingWeapon/Iai.cs", (43, 9) },
        { "LivingWeapon/IconCacheHide.Policy.cs", (42, 8) },
        { "LivingWeapon/IconCacheHide.Scan.cs", (15, 6) },
        { "LivingWeapon/IconCacheHide.Types.cs", (13, 4) },
        { "LivingWeapon/IconCacheHide.Write.cs", (6, 3) },
        { "LivingWeapon/IconCacheHide.cs", (18, 8) },
        { "LivingWeapon/IconGlow.Apply.cs", (19, 7) },
        { "LivingWeapon/IconGlow.Hide.cs", (12, 6) },
        { "LivingWeapon/IconGlow.Judge.cs", (12, 5) },
        { "LivingWeapon/IconGlow.Manifest.cs", (7, 4) },
        { "LivingWeapon/IconGlow.Pac.cs", (40, 18) },
        { "LivingWeapon/IconGlow.Policy.cs", (9, 4) },
        { "LivingWeapon/IconGlow.Store.cs", (25, 6) },
        { "LivingWeapon/IconGlow.StoreFile.cs", (15, 4) },
        { "LivingWeapon/IconGlow.cs", (25, 9) },
        { "LivingWeapon/ImageBase.cs", (13, 6) },
        { "LivingWeapon/InventoryResetHook.cs", (32, 20) },
        { "LivingWeapon/KillTally.cs", (16, 6) },
        { "LivingWeapon/KillTracker.Corpses.cs", (195, 22) },
        { "LivingWeapon/KillTracker.Delayed.cs", (30, 9) },
        { "LivingWeapon/KillTracker.Stamp.cs", (25, 8) },
        { "LivingWeapon/KillTracker.cs", (106, 16) },
        { "LivingWeapon/KillerStamp.cs", (37, 17) },
        { "LivingWeapon/KillsSchema.cs", (8, 5) },
        { "LivingWeapon/Kobu.Policy.cs", (7, 3) },
        { "LivingWeapon/Kobu.cs", (40, 32) },
        { "LivingWeapon/LandmarkStall.cs", (11, 7) },
        { "LivingWeapon/Larceny.Policy.cs", (44, 7) },
        { "LivingWeapon/Larceny.cs", (23, 10) },
        { "LivingWeapon/LarcenyHoldings.cs", (21, 6) },
        { "LivingWeapon/LaunchGuard.Doors.cs", (11, 4) },
        { "LivingWeapon/LaunchGuard.KitLane.cs", (20, 6) },
        { "LivingWeapon/LaunchGuard.Landmarks.cs", (27, 8) },
        { "LivingWeapon/LaunchGuard.Sensing.cs", (43, 14) },
        { "LivingWeapon/LaunchGuard.Stall.cs", (15, 6) },
        { "LivingWeapon/LaunchGuard.Stamp.cs", (23, 8) },
        { "LivingWeapon/LaunchGuard.cs", (32, 10) },
        { "LivingWeapon/LaunchHeader.cs", (4, 2) },
        { "LivingWeapon/LegendStore.cs", (30, 6) },
        { "LivingWeapon/ListBuilderHook.Policy.cs", (7, 2) },
        { "LivingWeapon/ListBuilderHook.Pool.cs", (26, 11) },
        { "LivingWeapon/ListBuilderHook.cs", (40, 9) },
        { "LivingWeapon/ListRelocation.Fields.cs", (15, 5) },
        { "LivingWeapon/ListRelocation.Sites.cs", (32, 12) },
        { "LivingWeapon/ListRelocation.cs", (31, 10) },
        { "LivingWeapon/LivingPoach.Despawn.cs", (15, 8) },
        { "LivingWeapon/LivingPoach.Policy.cs", (14, 8) },
        { "LivingWeapon/LivingPoach.Signals.cs", (16, 7) },
        { "LivingWeapon/LivingPoach.cs", (17, 8) },
        { "LivingWeapon/LogNames.cs", (6, 2) },
        { "LivingWeapon/LogVerb.cs", (5, 4) },
        { "LivingWeapon/Maim.Policy.cs", (25, 3) },
        { "LivingWeapon/Maim.cs", (36, 12) },
        { "LivingWeapon/Mem.cs", (25, 7) },
        { "LivingWeapon/MemBits.cs", (15, 5) },
        { "LivingWeapon/Mod.cs", (34, 6) },
        { "LivingWeapon/ModInfo.cs", (6, 5) },
        { "LivingWeapon/ModLogger.cs", (19, 6) },
        { "LivingWeapon/ModdedPac.Io.cs", (11, 5) },
        { "LivingWeapon/ModdedPac.cs", (37, 12) },
        { "LivingWeapon/Mushin.Policy.cs", (16, 8) },
        { "LivingWeapon/Mushin.cs", (41, 34) },
        { "LivingWeapon/NaturalLedger.cs", (16, 7) },
        { "LivingWeapon/NearAllocator.cs", (26, 8) },
        { "LivingWeapon/NullLogger.cs", (1, 1) },
        { "LivingWeapon/NumeralSpike.cs", (39, 8) },
        { "LivingWeapon/Offsets.cs", (448, 23) },
        { "LivingWeapon/OrderRebuildHook.Policy.cs", (5, 2) },
        { "LivingWeapon/OrderRebuildHook.Seat.cs", (17, 8) },
        { "LivingWeapon/OrderRebuildHook.cs", (37, 13) },
        { "LivingWeapon/OwnershipHold.cs", (20, 8) },
        { "LivingWeapon/PacScan.cs", (38, 12) },
        { "LivingWeapon/Part.cs", (6, 2) },
        { "LivingWeapon/PartsBoard.cs", (6, 5) },
        { "LivingWeapon/PartsReport.cs", (9, 4) },
        { "LivingWeapon/PaintOutcome.cs", (8, 6) },
        { "LivingWeapon/Plague.Baseline.cs", (10, 4) },
        { "LivingWeapon/Plague.Policy.cs", (23, 4) },
        { "LivingWeapon/Plague.cs", (37, 18) },
        { "LivingWeapon/PlagueState.cs", (22, 3) },
        { "LivingWeapon/PlaythroughReset.Policy.cs", (12, 4) },
        { "LivingWeapon/PlaythroughReset.cs", (25, 14) },
        { "LivingWeapon/PoachMap.cs", (13, 10) },
        { "LivingWeapon/PoolLocator.Log.cs", (15, 5) },
        { "LivingWeapon/PoolLocator.Policy.cs", (14, 7) },
        { "LivingWeapon/PoolLocator.Restart.cs", (60, 7) },
        { "LivingWeapon/PoolLocator.WarmStart.cs", (17, 8) },
        { "LivingWeapon/PoolLocator.cs", (21, 8) },
        { "LivingWeapon/PoolRegionSidecar.cs", (15, 8) },
        { "LivingWeapon/PoolScan.cs", (27, 10) },
        { "LivingWeapon/PromptSwap.Sampler.cs", (21, 7) },
        { "LivingWeapon/PromptSwap.cs", (32, 19) },
        { "LivingWeapon/PromptSwapHook.cs", (39, 22) },
        { "LivingWeapon/Provoke.Policy.cs", (36, 18) },
        { "LivingWeapon/Provoke.Table.cs", (33, 13) },
        { "LivingWeapon/Provoke.cs", (21, 13) },
        { "LivingWeapon/ProvokeHold.Policy.cs", (58, 27) },
        { "LivingWeapon/ProvokeHold.Scan.cs", (61, 7) },
        { "LivingWeapon/ProvokeHold.cs", (53, 10) },
        { "LivingWeapon/ProvokeSpike.cs", (21, 13) },
        { "LivingWeapon/Puppeteer.Hold.cs", (17, 5) },
        { "LivingWeapon/Puppeteer.Policy.cs", (63, 7) },
        { "LivingWeapon/Puppeteer.cs", (83, 61) },
        { "LivingWeapon/Rapture.Policy.cs", (31, 3) },
        { "LivingWeapon/Rapture.cs", (25, 13) },
        { "LivingWeapon/Reliquary.cs", (28, 6) },
        { "LivingWeapon/Renewal.Policy.cs", (9, 4) },
        { "LivingWeapon/Renewal.cs", (8, 7) },
        { "LivingWeapon/RestartSentinel.Policy.cs", (47, 16) },
        { "LivingWeapon/RestartSentinel.Stash.cs", (19, 6) },
        { "LivingWeapon/RestartSentinel.cs", (43, 9) },
        { "LivingWeapon/Ricochet.Policy.cs", (20, 4) },
        { "LivingWeapon/Ricochet.cs", (16, 11) },
        { "LivingWeapon/Sanctuary.Policy.cs", (3, 1) },
        { "LivingWeapon/Sanctuary.cs", (13, 8) },
        { "LivingWeapon/SaveEdgeHooks.Detours.cs", (16, 6) },
        { "LivingWeapon/SaveEdgeHooks.cs", (43, 33) },
        { "LivingWeapon/SaveEdgeTracker.cs", (14, 11) },
        { "LivingWeapon/SaveLocation.cs", (35, 7) },
        { "LivingWeapon/ScanCursor.cs", (16, 6) },
        { "LivingWeapon/ScopedLogger.cs", (11, 5) },
        { "LivingWeapon/ShadowBlade.Policy.cs", (22, 8) },
        { "LivingWeapon/ShadowBlade.cs", (28, 18) },
        { "LivingWeapon/ShopFlagsMirror.cs", (13, 8) },
        { "LivingWeapon/SidecarJson.cs", (8, 7) },
        { "LivingWeapon/SignatureParts.cs", (5, 4) },
        { "LivingWeapon/Signatures.cs", (58, 6) },
        { "LivingWeapon/SpiritualFont.Policy.cs", (32, 5) },
        { "LivingWeapon/SpiritualFont.cs", (20, 11) },
        { "LivingWeapon/StandDownNotice.cs", (14, 8) },
        { "LivingWeapon/StatusApply.cs", (23, 9) },
        { "LivingWeapon/StatusSpike.cs", (53, 9) },
        { "LivingWeapon/StoryLines.cs", (11, 6) },
        { "LivingWeapon/SuffixRotation.cs", (9, 5) },
        { "LivingWeapon/SwingIdFallbackHook.cs", (13, 9) },
        { "LivingWeapon/SyncOutcome.cs", (6, 5) },
        { "LivingWeapon/TallyMigration.cs", (19, 11) },
        { "LivingWeapon/TemplateRelocation.Sites.cs", (47, 9) },
        { "LivingWeapon/TemplateRelocation.cs", (24, 8) },
        { "LivingWeapon/TemplateSeat.Repair.cs", (14, 9) },
        { "LivingWeapon/TemplateSeat.cs", (48, 16) },
        { "LivingWeapon/TemplateSync.cs", (27, 11) },
        { "LivingWeapon/ThunkStub.Trampoline.cs", (10, 7) },
        { "LivingWeapon/ThunkStub.cs", (38, 13) },
        { "LivingWeapon/TickPhase.cs", (18, 5) },
        { "LivingWeapon/Tuning.cs", (240, 12) },
        { "LivingWeapon/TurnOrder.cs", (22, 9) },
        { "LivingWeapon/TurnOwnerProbe.cs", (39, 23) },
        { "LivingWeapon/TurnOwnerSpike.cs", (13, 9) },
        { "LivingWeapon/TurnTracker.cs", (38, 9) },
        { "LivingWeapon/Ultima.Policy.cs", (11, 5) },
        { "LivingWeapon/VictimClass.cs", (26, 5) },
        { "LivingWeapon/VictimProbe.cs", (17, 8) },
        { "LivingWeapon/VictimReader.cs", (11, 6) },
        { "LivingWeapon/WeaponMeta.cs", (57, 3) },
        { "LivingWeapon/WeaponPalette.Desire.cs", (11, 7) },
        { "LivingWeapon/WeaponPalette.Policy.cs", (32, 19) },
        { "LivingWeapon/WeaponPalette.cs", (34, 10) },
        { "LivingWeapon/Wielder.Ambiguous.cs", (15, 7) },
        { "LivingWeapon/Wielder.Dev.cs", (3, 2) },
        { "LivingWeapon/Wielder.Occupancy.cs", (8, 5) },
        { "LivingWeapon/Wielder.Roster.cs", (51, 5) },
        { "LivingWeapon/Wielder.cs", (36, 6) },
        { "LivingWeapon/WielderKeyedStore.cs", (37, 23) },
        { "LivingWeapon/WpScratchPainter.cs", (4, 2) },
        { "LivingWeapon/WpTableHold.cs", (33, 13) },
    };

    [Fact]
    public void Scan_covers_the_runtime_and_keys_are_unique()
    {
        var keys = ProductionFiles(RepoRoot()).Select(p => p.Key).ToList();
        Assert.True(keys.Count > 150, $"production sweep too small: {keys.Count}");
        var dupes = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "two production files share a filename, which would merge their rows: " + string.Join(", ", dupes));
    }

    [Theory]
    [InlineData("// plain words\nint x = 1;\n", 1, 1, 1, 0, 0, 0)]
    [InlineData("/// <summary>a: b</summary>\n/// second\n/// third\nvoid F() {}\n", 3, 1, 3, 1, 0, 0)]
    [InlineData("int x = 1; // trailing - with a dash\n", 0, 1, 0, 0, 1, 0)]
    [InlineData("// chosen deliberately, by design\n", 1, 0, 1, 0, 0, 2)]
    [InlineData("// at 0x14028CC0: see Offsets.cs:12 and 13:45\n", 1, 0, 1, 0, 0, 0)]
    [InlineData("///   - a bullet item\n/// says \"a: b\" in a string and `x: y` in code\n", 2, 0, 2, 0, 0, 0)]
    public void Measure_counts_what_the_rules_read(string text, int lines, int code, int block, int colons, int dashes, int tells)
    {
        var m = MeasureText(text);
        Assert.Equal((lines, code, block, colons, dashes, tells), (m.CommentLines, m.CodeLines, m.MaxBlock, m.Colons, m.Dashes, m.Tells.Count));
    }

    [Fact]
    public void No_comment_carries_a_tell_phrase_a_colon_or_a_dash()
    {
        var hits = Current(RepoRoot()).Where(kv => kv.Value.Tells.Count > 0 || kv.Value.Colons > 0 || kv.Value.Dashes > 0)
            .Select(kv => $"  {kv.Key}, {kv.Value.Colons} colons, {kv.Value.Dashes} dashes, tells {string.Join(", ", kv.Value.Tells)}").ToList();
        Assert.True(hits.Count == 0, "comments that argue, or carry a colon or dash inside a sentence (the owner writes neither):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Comment_budget_holds_against_the_frozen_baseline()
    {
        var current = Current(RepoRoot());
        var problems = new List<string>();
        var rows = new List<string>();
        foreach (var (key, m) in current.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string row = $"        {{ \"{key}\", ({m.CommentLines}, {m.MaxBlock}) }},";
            if (Baseline.TryGetValue(key, out var b))
            {
                int allowed = b.Lines + Math.Max(GrowthFloor, (int)Math.Ceiling(b.Lines * GrowthAllowance));
                if (m.CommentLines > allowed) problems.Add($"{key} has {m.CommentLines} comment lines, baseline {b.Lines} allows {allowed}");
                if (m.MaxBlock > b.Block) problems.Add($"{key} has a comment block of {m.MaxBlock} lines, baseline {b.Block}");
                if (m.CommentLines < b.Lines || m.MaxBlock < b.Block) rows.Add(row + "   // shrank, paste to tighten");
            }
            else
            {
                double ratio = m.CodeLines == 0 ? 0 : (double)m.CommentLines / m.CodeLines;
                if (ratio > NewFileMaxRatio) problems.Add($"{key} (new) has {m.CommentLines} comment lines over {m.CodeLines} code lines, cap is one in three");
                if (m.MaxBlock > NewFileMaxBlock) problems.Add($"{key} (new) has a comment block of {m.MaxBlock} lines, cap is {NewFileMaxBlock}");
                rows.Add(row + "   // new file, paste to freeze");
            }
        }
        foreach (var key in Baseline.Keys.Except(current.Keys)) rows.Add($"        // {key} no longer exists, drop its row");
        Assert.True(problems.Count == 0,
            "Comment budget exceeded.\n\n" + string.Join("\n", problems) +
            "\n\nIf the growth is earned, the owner raises the row; otherwise cut the comment.\n" +
            (rows.Count == 0 ? "" : "\nRows that moved (paste over the baseline entries):\n" + string.Join("\n", rows)));
    }
}
