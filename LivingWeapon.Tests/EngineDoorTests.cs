using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the engine over the parts registry. The doors step independently from the first tick, a closed
/// roster door no longer freezes the tick, a part behind a closed door is never ticked, and one report line
/// plus one box per closure wave reach the player. Every test runs the real Engine over FakeSparseMemory.
/// </summary>
public class EngineDoorTests
{
    private const string TwinMeta =
        "{\"200\":{\"name\":\"Twin Test Pistol\",\"wp\":7,\"cat\":\"Gun\",\"formula\":3,\"signature\":{\"abilityId\":0,\"slot\":\"\",\"atTier\":3,\"gunSlinger\":true}}}";

    private static Engine Build(TempDirs temp, FakeSparseMemory mem, List<(string title, string text)>? notices = null,
        bool drill = false, bool withTwinMeta = false, bool bandDrill = false, bool twinDrill = false, bool tableDrill = false)
    {
        string modDir = EngineTests.NestedModDir(temp);
        if (withTwinMeta) File.WriteAllText(Path.Combine(modDir, "meta.json"), TwinMeta);
        return new Engine(modDir, devSeedKills: false, devForceRosterMismatch: drill, devForceBandMismatch: bandDrill, devForceTwinMismatch: twinDrill,
            devForceTableMismatch: tableDrill, mem: mem, notice: (t, x) => notices?.Add((t, x)));
    }

    private static void Idle(FakeSparseMemory mem)
        => EngineTests.StageSentinelsBothStores(mem, 0xFFFFFFFFu, 0xFFFFFFFFu, 255, 0xFFFF, 0, 0);

    private static void Ticks(Engine e, int n) { for (int i = 0; i < n; i++) e.Tick(); }

    [Fact]
    public void Lanes_arm_before_the_roster_door()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_lanes_");
            var mem = EngineTests.HealthyMemory(level: 0);   // no save loaded, the roster row is empty
            Idle(mem);
            var engine = Build(temp, mem);

            Ticks(engine, 400);

            Assert.Equal(GuardState.Verifying, engine.Guard.State);
            Assert.True(engine.Guard.KitLaneArmed);
            Assert.True(engine.Guard.SensingArmed);
            Assert.False(Mem.WritesEnabled);
            Assert.Contains("battle sensing ON", engine.Board.LastLine);
            Assert.Contains("kill counting WAITING", engine.Board.LastLine);
            Assert.Contains("twin weapons WAITING", engine.Board.LastLine);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Tick_keeps_sensing_while_the_roster_door_is_closed()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_closed_");
            var mem = EngineTests.HealthyMemory(nameId: 5);   // Ramza's row reads wrong
            Idle(mem);
            var engine = Build(temp, mem);

            Ticks(engine, 40);
            Assert.Equal(GuardState.StoodDown, engine.Guard.State);
            int before = mem.ReadCount[Offsets.Slot0];

            Ticks(engine, 10);

            Assert.True(mem.ReadCount[Offsets.Slot0] > before, "the tick returned early after the roster stand down");
            Assert.False(Mem.WritesEnabled);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Closed_roster_never_ticks_the_gunslinger_row_and_an_open_one_does()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            long slotOneLevel = Offsets.RosterBase + Offsets.RosterStride + Offsets.RLevel;   // only the gunslinger row reads slot 1

            // Control, so the closed half cannot pass vacuously. A healthy roster reads the off-hand word.
            using var tempOpen = TempDirs.Create("lw_door_gun_open_");
            var open = EngineTests.HealthyMemory();
            Idle(open);
            Ticks(Build(tempOpen, open, withTwinMeta: true), 100);
            Assert.True(open.ReadCount.ContainsKey(slotOneLevel), "control engine never reached the gunslinger row");

            using var tempClosed = TempDirs.Create("lw_door_gun_closed_");
            var closed = EngineTests.HealthyMemory(nameId: 5);
            Idle(closed);
            var engine = Build(tempClosed, closed, withTwinMeta: true);
            Ticks(engine, 100);

            Assert.Equal(GuardState.StoodDown, engine.Guard.State);
            Assert.False(closed.ReadCount.ContainsKey(slotOneLevel));
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void One_parts_report_line_reaches_the_logger_per_door_edge()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_lines_");
            using var cap = LogCapture.Start();
            var mem = EngineTests.HealthyMemory();
            Idle(mem);
            var notices = new List<(string title, string text)>();
            var engine = Build(temp, mem, notices);

            Ticks(engine, 60);
            Assert.Equal(1, cap.File.Count(l => l.Contains(PartsReport.PartsMark)));   // roster, lanes open, hooks and extended still waiting

            engine.InjectHooks(null);   // no loader hooks, no extended items shipped: two absences
            Ticks(engine, 60);

            Assert.Equal(2, cap.File.Count(l => l.Contains(PartsReport.PartsMark)));
            Assert.Empty(notices);   // absences and an all clear never box
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Roster_drill_through_the_engine_boxes_once_with_the_roster_parts_off_and_the_rest_listed()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_drill_");
            var mem = EngineTests.HealthyMemory();
            Idle(mem);
            var notices = new List<(string title, string text)>();
            var engine = Build(temp, mem, notices, drill: true);

            Ticks(engine, 30 + PartsBoard.SettleSteps + 60);

            Assert.Equal(GuardState.StoodDown, engine.Guard.State);
            Assert.False(Mem.WritesEnabled);
            var box = Assert.Single(notices);
            string text = box.text;
            int working = text.IndexOf("Still working:", StringComparison.Ordinal);
            int off = text.IndexOf("Switched off:", StringComparison.Ordinal);
            int waiting = text.IndexOf("Still waiting:", StringComparison.Ordinal);
            Assert.True(working >= 0 && off > working && waiting > off);
            Assert.InRange(text.IndexOf("battle sensing", StringComparison.Ordinal), working, off);
            Assert.InRange(text.IndexOf("kill counting", StringComparison.Ordinal), off, waiting);
            Assert.InRange(text.IndexOf("twin weapons", StringComparison.Ordinal), off, waiting);
            Assert.True(text.IndexOf("banners", waiting, StringComparison.Ordinal) > waiting);
            Assert.True(text.IndexOf("extended inventory", waiting, StringComparison.Ordinal) > waiting);
            Assert.Contains("LW_FORCE_ROSTER_MISMATCH", text);
            Assert.Equal(1, engine.Board.BoxCount);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    // ---- LW-398: the band and static array doors through the engine ----

    private static void OnField(FakeSparseMemory mem)
        => EngineTests.StageSentinelsBothStores(mem, 0x10, 0xFFFFFFFFu, 3, 0xFFFF, 0, 0);

    private static FakeSparseMemory BattleMemory()
    {
        var mem = EngineTests.HealthyMemory();
        var band = BattleRegionShapeTests.EmptyBand();
        BattleRegionShapeTests.PutBand(band, 3, 12, 70, 65, 300, 300, nameId: 1);
        var arr = BattleRegionShapeTests.EmptyArray();
        BattleRegionShapeTests.PutArray(arr, 4, 300, 12, 70, 65);
        mem.TerrainBlocks[Offsets.FrameReadBase] = band;
        mem.TerrainBlocks[Offsets.ArrayReadBase] = arr;
        Idle(mem);
        return mem;
    }

    [Fact]
    public void Closed_band_door_never_ticks_the_kill_poll_row_while_the_roster_is_open()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_band_closed_");
            var mem = BattleMemory();
            var engine = Build(temp, mem, bandDrill: true);
            Ticks(engine, 40);   // the roster and the lanes arm on the idle world map
            OnField(mem);

            Ticks(engine, LaunchGuard.BattleGraceSteps + 30 + 5);

            var band = engine.Guard.BandDoor;
            Assert.Equal(DoorState.Closed, band.State);
            Assert.True(band.FailedCheck);
            Assert.Equal(DoorState.Open, engine.Guard.StaticArrayDoor.State);
            int acted = mem.ReadCount.GetValueOrDefault(Offsets.Acted);
            int actor = mem.ReadCount.GetValueOrDefault(Offsets.ActorPtr);

            Ticks(engine, 20);

            Assert.Equal(acted, mem.ReadCount.GetValueOrDefault(Offsets.Acted));
            Assert.Equal(actor, mem.ReadCount.GetValueOrDefault(Offsets.ActorPtr));
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Open_band_door_ticks_the_kill_poll_row()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_band_open_");
            var mem = BattleMemory();
            var engine = Build(temp, mem);
            Ticks(engine, 40);
            OnField(mem);
            Ticks(engine, 10);

            Assert.Equal(DoorState.Open, engine.Guard.BandDoor.State);
            Assert.Equal(DoorState.Open, engine.Guard.StaticArrayDoor.State);
            int acted = mem.ReadCount.GetValueOrDefault(Offsets.Acted);
            int actor = mem.ReadCount.GetValueOrDefault(Offsets.ActorPtr);

            Ticks(engine, 20);

            Assert.True(mem.ReadCount.GetValueOrDefault(Offsets.Acted) > acted, "turn-poll did not tick");
            Assert.True(mem.ReadCount.GetValueOrDefault(Offsets.ActorPtr) > actor, "kill-poll did not tick");
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Before_the_first_battle_the_report_lists_the_three_parts_waiting()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_prebattle_");
            var mem = BattleMemory();
            var engine = Build(temp, mem);

            Ticks(engine, 40);

            Assert.Equal(GuardState.Armed, engine.Guard.State);
            Assert.Contains("kill counting WAITING (waiting for the first battle)", engine.Board.LastLine);
            Assert.Contains("stat growth WAITING (waiting for the first battle)", engine.Board.LastLine);
            Assert.Contains($"{PartsReport.LabelOf(Part.BandSignatures)} WAITING (waiting for the first battle)", engine.Board.LastLine);
            Assert.Contains("card paint ON", engine.Board.LastLine);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Band_drill_through_the_engine_boxes_once_with_three_parts_off_and_the_rest_listed()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_band_drill_");
            var mem = BattleMemory();
            var notices = new List<(string title, string text)>();
            var engine = Build(temp, mem, notices, bandDrill: true);
            Ticks(engine, 40);
            OnField(mem);

            Ticks(engine, LaunchGuard.BattleGraceSteps + 30 + PartsBoard.SettleSteps + 60);

            var box = Assert.Single(notices);
            string text = box.text;
            int working = text.IndexOf("Still working:", StringComparison.Ordinal);
            int off = text.IndexOf("Switched off:", StringComparison.Ordinal);
            Assert.True(working >= 0 && off > working);
            string offLine = text.Substring(off, text.IndexOf('\n', off) - off);
            foreach (var part in new[] { Part.CardPaint, Part.WeaponCommands })
                Assert.InRange(text.IndexOf(PartsReport.LabelOf(part), StringComparison.Ordinal), working, off);
            foreach (var part in new[] { Part.TwinPistols, Part.Bulwark })
                Assert.DoesNotContain(PartsReport.LabelOf(part), offLine);
            foreach (var part in new[] { Part.KillCounting, Part.StatGrowth, Part.BandSignatures })
                Assert.True(text.IndexOf(PartsReport.LabelOf(part), off, StringComparison.Ordinal) > off);
            Assert.Contains("LW_FORCE_BAND_MISMATCH", text);
            Assert.DoesNotContain("LW_FORCE_ROSTER_MISMATCH", text);
            Assert.Equal(1, engine.Board.BoxCount);

            Ticks(engine, 300);

            Assert.Equal(1, engine.Board.BoxCount);
            Assert.Single(notices);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Closed_twin_door_never_ticks_the_gunslinger_row_while_kill_counting_stays_on()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            long slotOneLevel = Offsets.RosterBase + Offsets.RosterStride + Offsets.RLevel;

            using var tempOpen = TempDirs.Create("lw_door_twin_open_");
            var open = EngineTests.HealthyMemory();
            Idle(open);
            Ticks(Build(tempOpen, open, withTwinMeta: true), 100);
            Assert.True(open.ReadCount.ContainsKey(slotOneLevel), "control engine never reached the gunslinger row");

            using var tempClosed = TempDirs.Create("lw_door_twin_closed_");
            var closed = EngineTests.HealthyMemory();
            EngineTests.StageTwinFlags(closed, 7, 0);
            Idle(closed);
            var engine = Build(tempClosed, closed, withTwinMeta: true);
            Ticks(engine, 100);

            Assert.Equal(GuardState.Armed, engine.Guard.State);
            Assert.Equal(GuardState.StoodDown, engine.Guard.TwinState);
            Assert.False(closed.ReadCount.ContainsKey(slotOneLevel));
            Assert.Contains("twin weapons OFF (", engine.Board.LastLine);
            Assert.Contains("kill counting WAITING (waiting for the first battle)", engine.Board.LastLine);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Twin_drill_through_the_engine_boxes_once_naming_the_twin_flag()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_twin_drill_");
            var mem = EngineTests.HealthyMemory();
            Idle(mem);
            var notices = new List<(string title, string text)>();
            var engine = Build(temp, mem, notices, twinDrill: true);

            Ticks(engine, 30 + PartsBoard.SettleSteps + 60);

            var box = Assert.Single(notices);
            string text = box.text;
            int working = text.IndexOf("Still working:", StringComparison.Ordinal);
            int off = text.IndexOf("Switched off:", StringComparison.Ordinal);
            Assert.True(working >= 0 && off > working);
            Assert.Contains("Switched off: twin weapons", text);
            Assert.DoesNotContain("kill counting", text.Substring(off, text.IndexOf('\n', off) - off));
            Assert.Contains("LW_FORCE_TWIN_MISMATCH", text);
            Assert.DoesNotContain("LW_FORCE_ROSTER_MISMATCH", text);
            Assert.Equal(1, engine.Board.BoxCount);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Table_doors_open_at_boot_and_feed_the_report()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            // No save loaded: every roster part waits on the roster reason (the first door in its row), but the table doors are independent.
            using var tempA = TempDirs.Create("lw_door_tables_a_");
            var noSave = EngineTests.HealthyMemory(level: 0);
            Idle(noSave);
            TableDoorsTests.StageTerrain(noSave);
            TableDoorsTests.StagePalette(noSave);
            TableDoorsTests.StagePoach(noSave);
            TableDoorsTests.StageProvoke(noSave, filled: false);
            TableDoorsTests.StageItems(noSave, filled: false);
            var asleep = Build(tempA, noSave);
            Ticks(asleep, 3);
            Assert.Equal(DoorState.Open, asleep.Tables.TerrainGrid.State);
            Assert.Equal(DoorState.Open, asleep.Tables.PaletteBanks.State);
            Assert.Equal(DoorState.Open, asleep.Tables.PoachStore.State);
            Assert.Equal(DoorState.Waiting, asleep.Tables.ProvokeTables.State);
            Assert.Equal(DoorState.Waiting, asleep.Tables.ItemStats.State);
            foreach (string label in new[] { "bulwark", "weapon colors", "poach den", "provoke", "gun growth" })
                Assert.Contains($"{label} WAITING (the game cannot be read yet)", asleep.Board.LastLine);

            // A save is loaded: only the two boot filled tables still hold their parts back, each for its own reason.
            using var tempB = TempDirs.Create("lw_door_tables_b_");
            var loaded = EngineTests.HealthyMemory();
            Idle(loaded);
            TableDoorsTests.StageTerrain(loaded);
            TableDoorsTests.StagePalette(loaded);
            TableDoorsTests.StagePoach(loaded);
            TableDoorsTests.StageProvoke(loaded, filled: false);
            TableDoorsTests.StageItems(loaded, filled: false);
            var engine = Build(tempB, loaded);
            Ticks(engine, 3);
            Assert.Contains("bulwark ON", engine.Board.LastLine);
            Assert.Contains("weapon colors ON", engine.Board.LastLine);
            Assert.Contains("poach den ON", engine.Board.LastLine);
            Assert.Contains("weapon commands ON", engine.Board.LastLine);
            Assert.Contains("provoke WAITING (waiting for the game to load its ability data)", engine.Board.LastLine);
            Assert.Contains("gun growth WAITING (waiting for the game to load its item data)", engine.Board.LastLine);

            TableDoorsTests.StageProvoke(loaded, filled: true);
            TableDoorsTests.StageItems(loaded, filled: true);
            Ticks(engine, 1);
            Assert.Contains("provoke ON", engine.Board.LastLine);
            Assert.Contains("gun growth ON", engine.Board.LastLine);
            Assert.DoesNotContain("WAITING (waiting for the game", engine.Board.LastLine);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Table_drill_boxes_once_with_weapon_colors_off()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_table_drill_");
            var mem = EngineTests.HealthyMemory();
            Idle(mem);
            TableDoorsTests.StageAll(mem);
            var notices = new List<(string title, string text)>();
            var engine = Build(temp, mem, notices, tableDrill: true);

            Ticks(engine, 30 + PartsBoard.SettleSteps + 80);

            var box = Assert.Single(notices);
            int off = box.text.IndexOf("Switched off:", StringComparison.Ordinal);
            Assert.True(off >= 0);
            string offLine = box.text.Substring(off, box.text.IndexOf('\n', off) - off);
            Assert.Contains("weapon colors", offLine);
            foreach (string other in new[] { "bulwark", "gun growth", "poach den", "provoke", "kill counting", "twin weapons" })
                Assert.DoesNotContain(other, offLine);
            Assert.Contains(PartsReport.TableDrillSentence, box.text);
            Assert.DoesNotContain("LW_FORCE_ROSTER_MISMATCH", box.text);
            Assert.Equal(GuardState.Armed, engine.Guard.State);
        }
        finally { Mem.WritesEnabled = prev; }
    }

    [Fact]
    public void Roster_and_table_drills_compose_both_sentences()
    {
        bool prev = Mem.WritesEnabled;
        try
        {
            Mem.WritesEnabled = false;
            using var temp = TempDirs.Create("lw_door_both_drills_");
            var mem = EngineTests.HealthyMemory();
            Idle(mem);
            TableDoorsTests.StageAll(mem);
            var notices = new List<(string title, string text)>();
            var engine = Build(temp, mem, notices, drill: true, tableDrill: true);

            Ticks(engine, 30 + PartsBoard.SettleSteps + 80);

            var box = Assert.Single(notices);
            Assert.Contains(PartsReport.RosterDrillSentence + " " + PartsReport.TableDrillSentence, box.text);
        }
        finally { Mem.WritesEnabled = prev; }
    }
}
