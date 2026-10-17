using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// Every paused part line reads in one voice. Each case stages a REAL stand down through the
/// rig its own door tests use and returns the single WARN or ERROR line it wrote, so nothing here is a copy of a string.
/// The order is fixed. What is paused and why, the hedge, what to do, an optional drill sentence, the detail clause last.
/// </summary>
public class PartsCopyContractTests
{
    private const int Debounce = 30;
    private const string Detail = "(detail:";

    private static string OnlyLine(LogCapture cap, string level)
        => Assert.Single(cap.File, l => l.Contains("[" + level + "]"));

    private static T WithWritesRestored<T>(Func<T> body)
    {
        bool prev = Mem.WritesEnabled;
        try { Mem.WritesEnabled = false; return body(); }
        finally { Mem.WritesEnabled = prev; }
    }

    private static string Roster(bool drill = false) => WithWritesRestored(() =>
    {
        using var cap = LogCapture.Start();
        var guard = new LaunchGuard(LaunchGuardTests.HealthyMemory(nameId: 5), forceMismatch: false, forceRosterMismatch: drill);
        for (int i = 0; i < 40; i++) guard.Step();
        return OnlyLine(cap, "ERROR");
    });

    private static string KitLane(bool unknownBuild = false) => WithWritesRestored(() =>
    {
        using var cap = LogCapture.Start();
        var mem = LaunchGuardTests.HealthyMemory();
        mem.TerrainBlocks[Barrage.AbilityBase + 8L * Barrage.RecSize] = new byte[] { 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA };
        var guard = new LaunchGuard(mem, forceMismatch: unknownBuild);
        for (int i = 0; i < 40; i++) { guard.Step(); guard.StepKitLane(); }
        return Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("jobcommand-table"));
    });

    private static string Sensing() => WithWritesRestored(() =>
    {
        using var cap = LogCapture.Start();
        var mem = LaunchGuardTests.HealthyMemory();
        LaunchGuardTests.StageSentinels(mem, 0xFFFFFFFFu, 1u, 255, 0xFFFF, 0, 0);
        var guard = new LaunchGuard(mem, forceMismatch: false);
        guard.Step();
        for (int i = 0; i < LaunchGuard.SensingBootGraceSteps + Debounce; i++) guard.StepSensing();
        return OnlyLine(cap, "WARN");
    });

    private static string Twin() => WithWritesRestored(() =>
    {
        using var cap = LogCapture.Start();
        var mem = LaunchGuardTests.HealthyMemory();
        LaunchGuardTests.StageTwinFlags(mem, 2, 0);
        var guard = new LaunchGuard(mem, forceMismatch: false);
        for (int i = 0; i < Debounce; i++) guard.StepTwin();
        return OnlyLine(cap, "WARN");
    });

    private static string Battle(bool band, bool drill = false) => WithWritesRestored(() =>
    {
        using var cap = LogCapture.Start();
        var rig = new BattleDoorTests.Rig(drill);
        if (!band) rig.Mirror();
        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);
        return OnlyLine(cap, "WARN");
    });

    private static string Table(long site, bool drill = false)
    {
        using var cap = LogCapture.Start();
        var mem = new FakeSparseMemory();
        TableDoorsTests.StageAll(mem);
        TableDoorsTests.Flip(mem, site);
        var doors = new TableDoors(mem, () => "", () => "", () => true, forceMismatch: drill);
        for (int i = 0; i < Debounce; i++) doors.Step();
        return OnlyLine(cap, "WARN");
    }

    private static string ExtendedInstall()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lw_partscopy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var cap = LogCapture.Start();
            var patcher = ExtendedInventoryTests.VanillaImage();
            patcher.Seed(ExtendedSites.BootPatches(1)[0].Addr, 0x07);
            var inv = new ExtendedInventory(patcher, new FakeNearAllocator(), ExtendedInventoryDoorTests.Moonblade(),
                ExtendedBagSidecar.Load(Path.Combine(dir, ExtendedBagSidecar.FileName)), _ => null);
            inv.BootArm(null);
            return OnlyLine(cap, "WARN");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string PromptPrologue()
    {
        using var cap = LogCapture.Start();
        PromptSwapHookDoorTests.NewHook(new FakeSparseMemory()).Arm(null!);
        return OnlyLine(cap, "WARN");
    }

    // The 11 byte prologue PromptSwapHook checks, staged so the arm reaches CreateHook. A null controller then throws inside the try.
    private static string PromptCatch()
    {
        using var cap = LogCapture.Start();
        var mem = new FakeSparseMemory();
        mem.TerrainBlocks[ImageBase.At(0x1403F109C)] = new byte[] { 0x40, 0x55, 0x53, 0x57, 0x48, 0x8B, 0xEC, 0x48, 0x83, 0xEC, 0x50 };
        PromptSwapHookDoorTests.NewHook(mem).Arm(null!);
        return OnlyLine(cap, "WARN");
    }

    private static string Stall() => WithWritesRestored(() =>
    {
        using var cap = LogCapture.Start();
        var guard = new LaunchGuard(new FakeSparseMemory(), forceMismatch: false);
        for (int i = 0; i < LaunchGuard.ImageStallSteps + 1; i++) guard.Step();
        return OnlyLine(cap, "WARN");
    });

    public static IEnumerable<object[]> Cases() => new[]
    {
        Case("roster", () => Roster()),
        Case("kit lane", () => KitLane()),
        Case("sensing", Sensing),
        Case("band", () => Battle(band: true)),
        Case("static array", () => Battle(band: false)),
        Case("twin", Twin),
        Case("terrain", () => Table(TableSignatures.TerrainVetoTestSite)),
        Case("palette", () => Table(TableSignatures.PaletteClearSite)),
        Case("poach", () => Table(TableSignatures.PoachStoreSiteA)),
        Case("provoke", () => Table(TableSignatures.InflictTableSite)),
        Case("items", () => Table(TableSignatures.ItemStatsSite)),
        Case("extended inventory install", ExtendedInstall),
        Case("prompt hook prologue", PromptPrologue),
        Case("prompt hook catch", PromptCatch),
        Case("stall", Stall, door: false),
    };

    private static object[] Case(string label, Func<string> stage, bool door = true) => new object[] { label, stage, door };

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_disabling_line_speaks_in_the_shared_order(string label, Func<string> stage, bool door)
    {
        string line = stage();

        Assert.True(Count(line, PartsCopy.WhatToDo) == 1, label + " carries the what to do sentence once");
        int what = line.IndexOf(PartsCopy.WhatToDo, StringComparison.Ordinal);
        string beforeWhat = line.Substring(0, what);
        if (door)
        {
            Assert.True(Count(line, PartsCopy.Hedge) == 1, label + " carries the hedge once");
            int hedge = line.IndexOf(PartsCopy.Hedge, StringComparison.Ordinal);
            int detail = line.IndexOf(Detail, StringComparison.Ordinal);
            Assert.True(hedge < what && what < detail, label + " keeps hedge, what to do, detail in order");
            Assert.EndsWith(")", line);
            Assert.Equal(1, Count(line, Detail));
            beforeWhat = line.Substring(0, hedge);
        }
        foreach (string banned in new[] { "0x", "TimeDateStamp", "prologue", "landmark", "sentinel", "wrong address" })
            Assert.DoesNotContain(banned, beforeWhat);
        if (door)
        {
            // The roster line keeps the scanner's own words, standing down, in place of paused.
            if (label != "roster") Assert.Contains("paused for this session", beforeWhat);
            Assert.DoesNotContain(" off for this session", line);
            Assert.DoesNotContain("switched off", line);
        }
    }

    [Fact]
    public void The_theory_covers_every_door_plus_the_stall_and_the_prompt_hook_catch()
    {
        Assert.Equal(Enum.GetValues(typeof(Door)).Length + 2, Cases().Count());
    }

    [Fact]
    public void A_drill_sentence_sits_after_what_to_do_and_before_the_detail()
    {
        foreach (var (line, drill) in new[]
        {
            (Roster(drill: true), PartsReport.RosterDrillSentence),
            (Battle(band: true, drill: true), PartsReport.BandDrillSentence),
            (Table(TableSignatures.PaletteClearSite, drill: true), PartsReport.TableDrillSentence),
        })
        {
            int what = line.IndexOf(PartsCopy.WhatToDo, StringComparison.Ordinal);
            int at = line.IndexOf(drill, StringComparison.Ordinal);
            Assert.True(what >= 0 && at > what, drill);
            Assert.True(at < line.IndexOf(Detail, StringComparison.Ordinal), drill);
        }
    }

    [Fact]
    public void An_unknown_build_sentence_sits_between_the_hedge_and_what_to_do()
    {
        string line = KitLane(unknownBuild: true);

        int hedge = line.IndexOf(PartsCopy.Hedge, StringComparison.Ordinal);
        int build = line.IndexOf(LaunchGuard.UnknownBuildHedge, StringComparison.Ordinal);
        int what = line.IndexOf(PartsCopy.WhatToDo, StringComparison.Ordinal);
        int detail = line.IndexOf(Detail, StringComparison.Ordinal);
        Assert.True(hedge >= 0 && hedge < build && build < what && what < detail);
        Assert.DoesNotContain("0x", line.Substring(0, detail));
        Assert.Contains("0x", line.Substring(detail));
    }

    [Fact]
    public void The_roster_stand_down_keeps_the_two_phrases_the_scanner_reads()
    {
        string line = Roster();

        Assert.Contains("standing down to protect your save", line);
        Assert.Contains("see the Parts report line for what still runs", line);
    }

    private const string Foot = "Living Weapons 7.7.7, game build 0x6A5E4A1B";

    [Fact]
    public void Every_box_form_carries_the_box_what_to_do_once_the_hedge_once_and_a_hex_only_in_the_footer()
    {
        var allClosed = new Dictionary<Door, DoorReading>();
        foreach (Door d in Enum.GetValues(typeof(Door))) allClosed[d] = DoorReading.Failed("the party data is not where this mod expects it");
        var someClosed = PartsReportTests.AllOpen();
        someClosed[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");

        foreach (string box in new[]
        {
            PartsReport.ComposeBox(allClosed, "", LaunchGuard.UnknownBuildHedge, Foot),
            PartsReport.ComposeBox(someClosed, PartsReport.RosterDrillSentence, LaunchGuard.UnknownBuildHedge, Foot),
            LaunchGuard.ImageStallBox + "\n\n" + Foot,
        })
        {
            Assert.Equal(1, Count(box, PartsCopy.WhatToDoBox));
            Assert.Equal(0, Count(box, PartsCopy.WhatToDo));
            Assert.Equal(1, Count(box, PartsCopy.Hedge));
            Assert.EndsWith("\n\n" + Foot, box);
            Assert.DoesNotContain("0x", box.Substring(0, box.Length - Foot.Length));
            Assert.DoesNotContain("TimeDateStamp", box);
        }
    }

    [Fact]
    public void The_plain_box_carries_the_box_what_to_do_once_and_no_hedge_or_hex_before_the_footer()
    {
        var plain = PartsReportTests.AllOpen();
        plain[Door.ExtendedArm] = DoorReading.FailedPlain("the new item data shipped with this build did not validate");

        string box = PartsReport.ComposeBox(plain, "", LaunchGuard.UnknownBuildHedge, Foot);

        Assert.Equal(1, Count(box, PartsCopy.WhatToDoBox));
        Assert.Equal(0, Count(box, PartsCopy.WhatToDo));
        Assert.Equal(0, Count(box, PartsCopy.Hedge));
        Assert.EndsWith("\n\n" + Foot, box);
        Assert.DoesNotContain("0x", box.Substring(0, box.Length - Foot.Length));
    }

    [Fact]
    public void The_cap_warnings_keep_the_developer_label_and_the_address_out_of_the_player_sentence()
    {
        var patch = new BytePatch(0x14027F002L, 0x06, 0x07, "shop-path sibling getter cap");
        var foreign = new PendingPatch(patch);
        var f = new FakeCodePatcher();
        f.Seed(patch.Addr, 0x90);
        Assert.True(foreign.Step(f));
        var locked = new PendingPatch(patch);
        var g = new FakeCodePatcher();
        g.Seed(patch.Addr, 0x06);
        g.RefuseWritesAt.Add(patch.Addr);
        Assert.True(locked.Step(g));

        string a = ExtendedInventory.CapWarn(foreign);
        string b = ExtendedInventory.CapWarn(locked);

        Assert.StartsWith("A spot in the game's item code was already changed by something else, so it is left alone and the new items may punch instead of swinging.", a);
        Assert.StartsWith("A spot in the game's item code could not be changed, so the new items may punch instead of swinging.", b);
        foreach (string line in new[] { a, b })
        {
            int detail = line.IndexOf(Detail, StringComparison.Ordinal);
            Assert.True(detail > 0);
            Assert.DoesNotContain("0x", line.Substring(0, detail));
            Assert.DoesNotContain("shop-path", line.Substring(0, detail));
            Assert.Contains("shop-path sibling getter cap", line.Substring(detail));
            Assert.Contains($"0x{patch.Addr:X}", line.Substring(detail));
            Assert.EndsWith(")", line);
        }
        Assert.Contains("reads 90", a);
    }

    [Fact]
    public void The_writer_refusals_are_follow_ups_and_do_not_repeat_what_to_do()
    {
        long[] sites =
        {
            TableSignatures.TerrainVetoTestSite, TableSignatures.PaletteClearSite, TableSignatures.PoachStoreSiteA,
            TableSignatures.InflictTableSite, TableSignatures.ItemStatsSite,
        };
        string[] writers = { "bulwark", "palette", "poach", "provoke", "gun" };
        bool prev = Mem.WritesEnabled;
        try
        {
            for (int i = 0; i < writers.Length; i++)
            {
                using var temp = TempDirs.Create("lw_partscopy_latch_");
                var engine = EngineTests.LatchEngine(temp, sites[i]);
                for (int t = 0; t < 31; t++) engine.Tick();
                using var cap = LogCapture.Start();
                Assert.False(engine.WriterLatches[writers[i]]!.Allows());
                Assert.DoesNotContain(PartsCopy.WhatToDo, OnlyLine(cap, "WARN"));
            }
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Extended_inventory_names_its_real_cause_on_each_of_the_three_paths()
    {
        string install = ExtendedInstall();
        Assert.Contains("game code they plug into", install);

        string dir = Path.Combine(Path.GetTempPath(), "lw_partscopy_ext_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var cap = LogCapture.Start();
            var bad = new ExtendedInventoryData.LoadResult { FolderPresent = true, Errors = new[] { "id 261: no row" } };
            var inv = new ExtendedInventory(ExtendedInventoryTests.VanillaImage(), new FakeNearAllocator(), bad,
                ExtendedBagSidecar.Load(Path.Combine(dir, ExtendedBagSidecar.FileName)), _ => null);
            inv.BootArm(null);
            string validate = OnlyLine(cap, "WARN");
            Assert.Contains("did not load correctly", validate);
            Assert.Contains("paused for this session", validate);
            Assert.DoesNotContain(PartsCopy.Hedge, validate);
            Assert.EndsWith("(detail: id 261: no row)", validate);
            Assert.Equal("the new item data shipped with this build did not validate", inv.Door.Reason);
            Assert.True(inv.Door.Plain);
            Assert.True(inv.Door.FailedCheck);

            using var cap2 = LogCapture.Start();
            new ExtendedInventory(ExtendedInventoryTests.VanillaImage(), new FakeNearAllocator(), ExtendedInventoryDoorTests.Moonblade(),
                ExtendedBagSidecar.Load(Path.Combine(dir, ExtendedBagSidecar.FileName))).BootArm(null);
            string hooks = OnlyLine(cap2, "WARN");
            Assert.EndsWith(PartsCopy.HooksWhatToDo, hooks);
            Assert.Contains("paused for this session", hooks);
            Assert.DoesNotContain(Detail, hooks);
            Assert.DoesNotContain(PartsCopy.WhatToDo, hooks);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
