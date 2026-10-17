using System;
using System.IO;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-425: a battle that fields no unit mirroring a party row, while the unit list agrees with the band, waits
/// instead of closing the band door. The fixtures are modeled on the observed 6.1 opener shapes and the New Game
/// roster row, because no tutorial bytes survive.
/// </summary>
public class BattleDoorNoMirrorTests
{
    private const string NoMirrorLine = "No unit in this battle matches a party member";
    private const string BandWarn = "live battle unit data is not where this mod expects it";

    private static BattleDoorTests.Rig NewGameRig() => new(level: 1, brave: 70, faith: 70);

    private static (int lvl, int br, int fa, int mhp, int nameId)[] OpeningUnits()
    {
        var u = new (int, int, int, int, int)[16];
        u[0] = (9, 67, 51, 157, 2);
        u[1] = (1, 70, 69, 55, 1);
        u[2] = (1, 69, 70, 56, 0);
        u[3] = (1, 70, 70, 57, 9);
        u[4] = (11, 70, 70, 58, 1);
        int[] hps = { 94, 108, 39, 43, 38 };
        for (int i = 5; i < 16; i++)
            u[i] = (5 + i, 40 + i, 41 + i, i < 10 ? hps[i - 5] : 200 + i, i % 2 == 0 ? 0 : 120 + i);
        return u;
    }

    private static void ScriptedOpening(BattleDoorTests.Rig rig)
    {
        var units = OpeningUnits();
        for (int i = 0; i < units.Length; i++)
        {
            var (lvl, br, fa, mhp, nameId) = units[i];
            BattleRegionShapeTests.PutBand(rig.Band, 8 + i, lvl, br, fa, mhp, mhp, nameId);
            BattleRegionShapeTests.PutArray(rig.Arr, i, mhp, lvl, br, fa);
        }
    }

    private static void OverwriteList(BattleDoorTests.Rig rig)
    {
        Array.Clear(rig.Arr);
        for (int i = 0; i < 16; i++) BattleRegionShapeTests.PutArray(rig.Arr, i, 900 + i, 50 + i, 5, 6);
    }

    private static void RestoreList(BattleDoorTests.Rig rig)
    {
        Array.Clear(rig.Arr);
        var units = OpeningUnits();
        for (int i = 0; i < units.Length; i++)
            BattleRegionShapeTests.PutArray(rig.Arr, i, units[i].mhp, units[i].lvl, units[i].br, units[i].fa);
    }

    private static void Gariland(BattleDoorTests.Rig rig)
    {
        Array.Clear(rig.Band);
        BattleRegionShapeTests.PutBand(rig.Band, 24, 1, 70, 70, 55, 55, nameId: 1);
    }

    [Fact]
    public void New_game_opening_battles_with_no_party_mirror_wait_and_Gariland_arms()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = NewGameRig();
        ScriptedOpening(rig);

        rig.Field(22);
        for (int i = 0; i < 26; i++) rig.G.StepBattleDoors(false, false, false);
        rig.Field(248);
        rig.Field(4);
        for (int i = 0; i < 5; i++) rig.G.StepBattleDoors(false, false, false);
        rig.Field(23);
        rig.Field(333);
        rig.Field(2000);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Equal(DoorState.Waiting, rig.G.BandDoor.State);
        Assert.False(rig.G.BandDoor.FailedCheck);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));
        Assert.Empty(rig.Flushes);
        Assert.DoesNotContain(rig.Records, r => r.payload.Contains("stand-down"));
        Assert.Equal(0, cap.File.Count(l => l.Contains("live battle unit data read unexpected values")));
        Assert.Equal(DoorState.Open, rig.G.StaticArrayDoor.State);
        var note = Assert.Single(cap.File, l => l.Contains(NoMirrorLine));
        Assert.Contains("rows checked 1", note);
        Assert.Contains("2:L9 B67 F51 M157", note);

        for (int i = 0; i < 50; i++) rig.G.StepBattleDoors(false, false, false);
        rig.NewBattle(500);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));

        Gariland(rig);
        rig.NewBattle(1);

        Assert.Equal(GuardState.Armed, rig.G.BandState);
        Assert.Equal(1, rig.Armed("band door armed"));
    }

    [Fact]
    public void A_band_sharing_nothing_with_a_populated_list_still_closes_after_the_grace()
    {
        using var cap = LogCapture.Start();
        var rig = NewGameRig();
        ScriptedOpening(rig);
        OverwriteList(rig);

        rig.Field(LaunchGuard.BattleGraceSteps + 30);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        var warn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains(BandWarn));
        Assert.Contains("16 unit shapes, none mirrors a party row", warn);
        Assert.Equal("standdown", Assert.Single(rig.Flushes));
        Assert.True(rig.G.BandDoor.FailedCheck);
    }

    [Fact]
    public void An_unreadable_list_page_never_excuses_a_band_with_no_party_mirror()
    {
        var rig = NewGameRig();
        ScriptedOpening(rig);
        rig.Mem.TerrainBlocks.Remove(Offsets.ArrayReadBase);

        rig.Field(LaunchGuard.BattleGraceSteps + 30);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
    }

    [Fact]
    public void Band_drill_waits_in_a_battle_with_no_party_mirror_and_still_closes_once_the_party_is_fielded()
    {
        using var cap = LogCapture.Start();
        var rig = new BattleDoorTests.Rig(drill: true, level: 1, brave: 70, faith: 70);
        ScriptedOpening(rig);

        rig.Field(1000);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));

        Gariland(rig);
        rig.Populate(4);
        BattleRegionShapeTests.PutArray(rig.Arr, 5, 55, 1, 70, 70);
        rig.NewBattle(LaunchGuard.BattleGraceSteps + 30);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        Assert.Contains(cap.File, l => l.Contains("drill: LW_FORCE_BAND_MISMATCH"));
    }

    [Fact]
    public void The_no_mirror_note_is_logged_once_across_battles()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = NewGameRig();
        ScriptedOpening(rig);

        rig.Field(50);
        for (int i = 0; i < 50; i++) rig.G.StepBattleDoors(false, false, false);
        rig.NewBattle(50);

        Assert.Equal(1, cap.File.Count(l => l.Contains(NoMirrorLine)));
    }

    [Fact]
    public void The_list_is_read_at_most_once_per_step_while_both_doors_check()
    {
        var rig = NewGameRig();
        BattleRegionShapeTests.PutBand(rig.Band, 3, 9, 67, 51, 157, 157, nameId: 2);

        rig.Field(10);

        Assert.Equal(10, rig.Reads(Offsets.ArrayReadBase));
    }

    [Fact]
    public void After_the_grace_is_spent_a_short_list_disagreement_in_a_no_mirror_battle_waits()
    {
        var rig = NewGameRig();
        ScriptedOpening(rig);
        rig.Field(LaunchGuard.BattleGraceSteps + 30);
        Assert.Equal(GuardState.Verifying, rig.G.BandState);

        OverwriteList(rig);
        rig.Field(29);
        RestoreList(rig);
        rig.Field(100);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);

        OverwriteList(rig);
        rig.Field(30);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
    }

    [Fact]
    public void Two_no_mirror_battles_wait_and_a_party_battle_still_arms()
    {
        using var cap = LogCapture.Start();
        var rig = NewGameRig();
        ScriptedOpening(rig);

        rig.NewBattle(700);
        rig.NewBattle(700);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));

        Gariland(rig);
        rig.NewBattle(1);

        Assert.Equal(GuardState.Armed, rig.G.BandState);
    }

    [Fact]
    public void The_third_no_mirror_battle_closes_the_door_with_the_usual_warn_naming_the_battles()
    {
        using var cap = LogCapture.Start();
        var rig = NewGameRig();
        ScriptedOpening(rig);

        rig.NewBattle(700);
        rig.NewBattle(700);
        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        rig.NewBattle(700);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        var warn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains(BandWarn));
        Assert.Contains("3 battles in a row", warn);
        Assert.Equal("standdown", Assert.Single(rig.Flushes));
        Assert.Equal(LaunchGuard.BandClosedReason, rig.G.BandDoor.Reason);
        Assert.True(rig.G.BandDoor.FailedCheck);
    }

    [Fact]
    public void A_new_game_resets_the_no_mirror_battle_count()
    {
        var rig = NewGameRig();
        ScriptedOpening(rig);

        rig.NewBattle(700);
        rig.NewBattle(700);
        rig.G.NoteNewGame();
        rig.NewBattle(700);
        rig.NewBattle(700);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
    }

    [Fact]
    public void Battles_that_never_reach_a_field_frame_do_not_count()
    {
        var rig = NewGameRig();
        ScriptedOpening(rig);
        rig.NewBattle(700);

        for (int i = 0; i < 6; i++)
        {
            rig.G.StepBattleDoors(true, false, true);
            rig.Live(50);
        }

        rig.Field(LaunchGuard.BattleGraceSteps + 30);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
    }

    [Fact]
    public void An_enter_on_a_tick_that_is_not_live_still_marks_the_battle_boundary()
    {
        using var cap = LogCapture.Start();
        var rig = NewGameRig();
        ScriptedOpening(rig);

        rig.NewBattle(700);
        rig.G.StepBattleDoors(false, false, true);
        rig.Field(LaunchGuard.BattleGraceSteps + 30);
        rig.NewBattle(700);
        rig.Field(30);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        Assert.Contains(cap.File, l => l.Contains("3 battles in a row"));
    }

    [Fact]
    public void Engine_hands_the_enter_edge_to_the_door_and_the_new_game_reset()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepoRoot(), "LivingWeapon", "Engine.cs"));

        Assert.Contains(lines, l => l.Contains("_launchGuard.StepBattleDoors(inLive, onField, edge == BattleEdge.Entered);"));
        Assert.Contains(lines, l => l.Contains("if (newGame)") && l.Contains("_launchGuard.NoteNewGame();"));
    }

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
}
