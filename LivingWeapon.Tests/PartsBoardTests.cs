using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the stateful half of the parts report. It logs one line per door STATE edge and shows one
/// box per closure wave. Every test drives Observe with a hand built readings table, one call per tick.
/// </summary>
public class PartsBoardTests
{
    private const string Footer = "Living Weapons 9.9.9, game build 0x6A5E4A1B";

    private sealed class Rig
    {
        public readonly List<(string title, string text)> Notices = new();
        public readonly List<(string type, string payload)> Records = new();
        public readonly PartsBoard Board;
        public readonly Dictionary<Door, DoorReading> R = PartsReportTests.AllOpen();

        public Rig() { Board = new PartsBoard((t, x) => Notices.Add((t, x)), (t, p) => Records.Add((t, p))); }

        public void Tick(int n = 1, string hedge = "", string drill = "")
        {
            for (int i = 0; i < n; i++) Board.Observe(R, hedge, drill, () => Footer);
        }
    }

    private const string AllArmed = "Living Weapons: all parts armed.";

    private static int AllArmedLines(LogCapture cap) => cap.File.Count(l => l.Contains(AllArmed));

    private static int ReportLines(LogCapture cap) => cap.File.Count(l => l.Contains(PartsReport.PartsMark));

    [Fact]
    public void No_state_edge_means_no_new_line_and_a_reason_only_change_is_no_edge()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Roster] = DoorReading.Waiting("waiting for a save to load");

        rig.Tick(50);
        Assert.Equal(1, ReportLines(cap));

        rig.R[Door.Roster] = DoorReading.Waiting("the game cannot be read yet");
        rig.Tick(50);

        Assert.Equal(1, ReportLines(cap));
        Assert.Empty(rig.Notices);
        Assert.Single(rig.Records);
    }

    [Fact]
    public void The_first_observation_logs_one_line_and_one_flight_record_and_a_state_change_logs_another()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Roster] = DoorReading.Waiting("waiting for a save to load");

        rig.Tick();

        string line = Assert.Single(cap.File, l => l.Contains(PartsReport.PartsMark));
        Assert.Contains("[INFO]", line);
        var rec = Assert.Single(rig.Records);
        Assert.Equal("guard", rec.type);
        Assert.StartsWith("parts report changed (", rec.payload);
        Assert.Equal(PartsReport.ComposeLine(rig.R), rig.Board.LastLine);

        rig.R[Door.KitLane] = DoorReading.Waiting("the job command check has not run yet");
        rig.Tick();

        Assert.Equal(2, ReportLines(cap));
        Assert.Equal(2, rig.Records.Count);
    }

    [Fact]
    public void A_failed_closure_boxes_once_after_the_settle_window_and_a_later_closure_in_it_joins()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(10);

        rig.R[Door.KitLane] = DoorReading.Failed("another mod rewrote the job command data");
        rig.Tick();   // tick t, the wave opens
        Assert.True(rig.Board.WaveOpen);
        rig.Tick(4);
        rig.R[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");
        rig.Tick(PartsBoard.SettleSteps - 5);

        Assert.Empty(rig.Notices);   // one tick short of t + SettleSteps

        rig.Tick();

        var box = Assert.Single(rig.Notices);
        Assert.Equal("FFT Living Weapons", box.title);
        Assert.Equal(PartsReport.ComposeBox(rig.R, "", "", Footer), box.text);
        Assert.Contains("kill counting", box.text);
        Assert.Contains("weapon commands", box.text);
        Assert.False(rig.Board.WaveOpen);
        Assert.Equal(1, rig.Board.BoxCount);
        Assert.Contains(rig.Records, r => r.type == "guard" && r.payload.StartsWith("parts box shown ("));

        rig.Tick(500);
        Assert.Single(rig.Notices);
    }

    [Fact]
    public void A_closure_long_after_the_first_wave_opens_a_new_wave_and_a_second_box()
    {
        // The B1 test. One box per process would hide a late closure from the player.
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.KitLane] = DoorReading.Failed("lane closed");
        rig.Tick(PartsBoard.SettleSteps + 1);
        Assert.Single(rig.Notices);

        rig.Tick(5000);
        rig.R[Door.Roster] = DoorReading.Failed("roster closed");
        rig.Tick(PartsBoard.SettleSteps + 1);

        Assert.Equal(2, rig.Notices.Count);
        Assert.Contains("Paused this session:\n  kill counting", rig.Notices[1].text);
        Assert.Equal(2, rig.Board.BoxCount);
    }

    [Fact]
    public void Absence_closures_and_the_sensing_closure_never_box()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.PromptHook] = DoorReading.Absent("the loader gave no hooks");
        rig.R[Door.ExtendedArm] = DoorReading.Absent("no new items are shipped in this build");
        rig.R[Door.Sensing] = DoorReading.Absent("the battle sentinels read wrong, so only the new game detector is off");

        rig.Tick(PartsBoard.SettleSteps * 3);

        Assert.Empty(rig.Notices);
        Assert.False(rig.Board.WaveOpen);
        Assert.Equal(0, rig.Board.BoxCount);
    }

    [Fact]
    public void Waiting_then_all_open_never_boxes()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Roster] = DoorReading.Waiting("waiting for a save to load");
        rig.R[Door.KitLane] = DoorReading.Waiting("the job command check has not run yet");
        rig.Tick(200);

        rig.R[Door.Roster] = DoorReading.Open();
        rig.R[Door.KitLane] = DoorReading.Open();
        rig.Tick(PartsBoard.SettleSteps * 3);

        Assert.Empty(rig.Notices);
        Assert.Equal(0, rig.Board.BoxCount);
    }

    [Fact]
    public void The_box_carries_the_drill_sentence_and_the_hedge_the_caller_supplied_at_fire_time()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");

        rig.Tick(PartsBoard.SettleSteps + 1, LaunchGuard.UnknownBuildHedge, drill: PartsReport.RosterDrillSentence);

        var box = Assert.Single(rig.Notices);
        Assert.Equal(PartsReport.ComposeBox(rig.R, PartsReport.RosterDrillSentence, LaunchGuard.UnknownBuildHedge, Footer), box.text);
    }

    [Fact]
    public void A_null_notice_still_counts_the_box_and_never_throws()
    {
        using var cap = LogCapture.Start();
        var board = new PartsBoard(null, null);
        var r = PartsReportTests.AllOpen();
        r[Door.Roster] = DoorReading.Failed("x");

        for (int i = 0; i < PartsBoard.SettleSteps + 2; i++) board.Observe(r, "", "", () => "");

        Assert.Equal(1, board.BoxCount);
    }

    [Fact]
    public void A_closed_band_reading_boxes_once_after_the_settle_window_with_the_band_drill_sentence()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(10);

        rig.R[Door.Band] = DoorReading.Failed(LaunchGuard.BandClosedReason);
        rig.Tick(PartsBoard.SettleSteps + 1, drill: PartsReport.BandDrillSentence);
        rig.Tick(500, drill: PartsReport.BandDrillSentence);

        var box = Assert.Single(rig.Notices);
        Assert.Equal(PartsReport.ComposeBox(rig.R, PartsReport.BandDrillSentence, "", Footer), box.text);
        Assert.Contains("LW_FORCE_BAND_MISMATCH", box.text);
        Assert.Equal(1, rig.Board.BoxCount);
    }

    [Fact]
    public void A_waiting_band_never_boxes()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Band] = DoorReading.Waiting(LaunchGuard.BattleWaitingReason);
        rig.R[Door.StaticArray] = DoorReading.Waiting(LaunchGuard.ArrayFailedFirstReason);

        rig.Tick(PartsBoard.SettleSteps * 3);

        Assert.Empty(rig.Notices);
        Assert.False(rig.Board.WaveOpen);
    }

    [Fact]
    public void A_twin_closure_boxes_once_and_carries_the_sentence_it_was_given()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(10);
        rig.R[Door.TwinPistols] = DoorReading.Failed("the menu flags the twin weapon grant relies on do not read as expected");

        rig.Tick(PartsBoard.SettleSteps + 1, "", PartsReport.TwinDrillSentence);

        var box = Assert.Single(rig.Notices);
        Assert.EndsWith(PartsReport.TwinDrillSentence + "\n\n" + Footer, box.text);
        Assert.Contains("Paused this session:\n  twin weapons\n", box.text);
        rig.Tick(5000, "", PartsReport.TwinDrillSentence);
        Assert.Single(rig.Notices);
        Assert.Equal(1, rig.Board.BoxCount);
    }

    [Fact]
    public void A_door_no_shipped_part_depends_on_opens_no_wave_and_shows_no_box()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(5);
        rig.R[Door.PaletteBanks] = DoorReading.Failed("the weapon palette is not where this mod expects it");

        rig.Tick(PartsBoard.SettleSteps + 1, drill: PartsReport.TableDrillSentence);

        if (PartsReport.Ships(Part.WeaponColors))
        {
            Assert.Single(rig.Notices);
            return;
        }
        Assert.False(rig.Board.WaveOpen);
        Assert.Empty(rig.Notices);
        Assert.Equal(0, rig.Board.BoxCount);
        Assert.DoesNotContain("OFF", rig.Board.LastLine);
    }

    [Fact]
    public void A_terrain_door_closure_boxes_once_with_the_sentence_in_the_box()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(5);
        rig.R[Door.TerrainGrid] = DoorReading.Failed("the terrain data is not where this mod expects it");

        rig.Tick(PartsBoard.SettleSteps, drill: PartsReport.TableDrillSentence);
        Assert.Empty(rig.Notices);
        rig.Tick(1, drill: PartsReport.TableDrillSentence);

        var box = Assert.Single(rig.Notices);
        Assert.Contains("bulwark", box.text);
        Assert.EndsWith(PartsReport.TableDrillSentence + "\n\n" + Footer, box.text);
        Assert.Contains(rig.Records, r => r.payload == "parts box shown (1 parts off)");
        rig.Tick(500, drill: PartsReport.TableDrillSentence);
        Assert.Single(rig.Notices);
    }

    [Fact]
    public void An_empty_drill_sentence_adds_nothing_to_the_box()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.ItemStats] = DoorReading.Failed("the item data is not where this mod expects it");

        rig.Tick(PartsBoard.SettleSteps + 1);

        var box = Assert.Single(rig.Notices);
        Assert.DoesNotContain("LW_FORCE_", box.text);
        Assert.Equal(PartsReport.ComposeBox(rig.R, "", "", Footer), box.text);
    }

    [Fact]
    public void The_board_logs_the_all_armed_line_once_on_the_transition_instead_of_the_parts_list()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Roster] = DoorReading.Waiting("waiting for a save to load");
        rig.Tick(5);
        Assert.Equal(1, ReportLines(cap));
        Assert.Equal(0, AllArmedLines(cap));

        rig.R[Door.Roster] = DoorReading.Open();
        rig.Tick();

        string line = Assert.Single(cap.File, l => l.Contains(AllArmed));
        Assert.Contains("[INFO]", line);
        Assert.Equal(1, ReportLines(cap));
        Assert.Equal(PartsReport.ComposeLine(rig.R), rig.Board.LastLine);

        rig.Tick(500);
        Assert.Equal(1, AllArmedLines(cap));
        Assert.Equal(1, ReportLines(cap));
    }

    [Fact]
    public void The_all_armed_flight_record_keeps_the_full_parts_list_payload()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();

        rig.Tick();

        var rec = Assert.Single(rig.Records);
        Assert.Equal("parts report changed (" + PartsReport.ComposeLine(rig.R) + ")", rec.payload);
        Assert.Equal(1, AllArmedLines(cap));
    }

    [Fact]
    public void A_second_batch_of_unchanged_readings_is_held_by_the_unchanged_tick_gate()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(3);

        rig.R[Door.Band] = DoorReading.Open();
        rig.R[Door.StaticArray] = DoorReading.Open();
        rig.Tick(3);

        Assert.Equal(1, AllArmedLines(cap));
        Assert.Single(rig.Records);
    }

    [Fact]
    public void A_later_closure_logs_the_parts_list_again_and_the_all_armed_line_returns_on_the_next_transition()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Tick(3);
        Assert.Equal(1, AllArmedLines(cap));

        rig.R[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");
        rig.Tick();
        Assert.Equal(1, ReportLines(cap));
        Assert.Equal(1, AllArmedLines(cap));

        rig.R[Door.Roster] = DoorReading.Open();
        rig.Tick();

        Assert.Equal(2, AllArmedLines(cap));
        Assert.Equal(1, ReportLines(cap));
    }

    [Fact]
    public void The_box_ends_with_the_footer_the_caller_supplies_at_fire_time()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.R[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");

        rig.Tick(PartsBoard.SettleSteps + 1);

        var box = Assert.Single(rig.Notices);
        Assert.EndsWith("\n\n" + Footer, box.text);
    }
}
