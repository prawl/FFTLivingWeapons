using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the pure parts report. Six door readings in, ten parts out with ON, OFF or WAITING and a
/// plain reason, plus the one box text. Order assertions use index positions, never Contains alone.
/// </summary>
public class PartsReportTests
{
    internal static Dictionary<Door, DoorReading> AllOpen() => new()
    {
        [Door.Roster] = DoorReading.Open(),
        [Door.KitLane] = DoorReading.Open(),
        [Door.Sensing] = DoorReading.Open(),
        [Door.PromptHook] = DoorReading.Open(),
        [Door.ExtendedArm] = DoorReading.Open(),
        [Door.TwinPistols] = DoorReading.Open(),
    };

    private static readonly string[] Labels =
    {
        "battle sensing", "kill counting", "stat growth", "card paint", "weapon powers in battle (Maim, Plague and the rest)",
        "weapon powers that edit game tables (Bulwark, Provoke)", "weapon commands", "banners", "extended inventory", "twin weapons",
    };

    [Fact]
    public void Every_part_appears_exactly_once_battle_sensing_first_in_table_order()
    {
        string line = PartsReport.ComposeLine(AllOpen());

        int last = -1;
        foreach (var label in Labels)
        {
            int at = line.IndexOf(label + " ", StringComparison.Ordinal);
            Assert.True(at > last, $"'{label}' must come after the previous part in '{line}'");
            Assert.Equal(at, line.LastIndexOf(label + " ", StringComparison.Ordinal));
            last = at;
        }
    }

    [Fact]
    public void The_line_starts_with_the_scanner_mark()
    {
        Assert.StartsWith(PartsReport.PartsMark, PartsReport.ComposeLine(AllOpen()));
        Assert.Equal("Parts report:", PartsReport.PartsMark);
    }

    [Fact]
    public void All_open_gives_all_on_and_no_off_or_waiting()
    {
        string line = PartsReport.ComposeLine(AllOpen());

        Assert.DoesNotContain("OFF", line);
        Assert.DoesNotContain("WAITING", line);
        Assert.Equal(10, line.Split("ON").Length - 1);
    }

    [Fact]
    public void Roster_closed_turns_the_seven_roster_parts_off_and_keeps_the_rest_on()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");

        string[] off = { "kill counting", "stat growth", "card paint", "weapon powers in battle (Maim, Plague and the rest)",
                         "weapon powers that edit game tables (Bulwark, Provoke)", "weapon commands", "twin weapons" };
        foreach (var part in new[] { Part.KillCounting, Part.StatGrowth, Part.CardPaint, Part.BandSignatures,
                                     Part.StaticTableSignatures, Part.WeaponCommands, Part.TwinPistols })
        {
            var (state, reason) = PartsReport.Status(part, r);
            Assert.Equal(DoorState.Closed, state);
            Assert.Equal("the party data is not where this mod expects it", reason);
        }
        foreach (var part in new[] { Part.Sensing, Part.Banners, Part.ExtendedInventory })
            Assert.Equal(DoorState.Open, PartsReport.Status(part, r).State);
        r[Door.TwinPistols] = DoorReading.Failed("menu flags");
        Assert.Equal("the party data is not where this mod expects it", PartsReport.Status(Part.TwinPistols, r).Reason);

        string line = PartsReport.ComposeLine(r);
        foreach (var label in off)
            Assert.Contains($"{label} OFF (the party data is not where this mod expects it)", line);
        Assert.Contains("battle sensing ON", line);
        Assert.Contains("extended inventory ON", line);
    }

    [Fact]
    public void Banners_carry_the_note_while_kill_counting_is_not_on()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("x");
        Assert.Contains("banners ON (connected; nothing to announce while kill counting is off)", PartsReport.ComposeLine(r));
        Assert.DoesNotContain("connected;", PartsReport.ComposeLine(AllOpen()));
    }

    [Fact]
    public void Kit_lane_closed_alone_turns_only_weapon_commands_off()
    {
        var r = AllOpen();
        r[Door.KitLane] = DoorReading.Failed("another mod rewrote the job command data");

        string line = PartsReport.ComposeLine(r);

        Assert.Contains("weapon commands OFF (another mod rewrote the job command data)", line);
        Assert.Single(line.Split("OFF").Skip(1));
        Assert.Equal(9, line.Split("ON").Length - 1);
    }

    [Fact]
    public void Roster_waiting_gives_waiting_with_the_waiting_reason()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Waiting("waiting for a save to load");

        var (state, reason) = PartsReport.Status(Part.KillCounting, r);

        Assert.Equal(DoorState.Waiting, state);
        Assert.Equal("waiting for a save to load", reason);
        Assert.Contains("twin weapons WAITING (waiting for a save to load)", PartsReport.ComposeLine(r));
    }

    [Fact]
    public void A_closed_door_outranks_a_waiting_one_and_the_first_in_door_order_names_the_reason()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Waiting("waiting for a save to load");
        r[Door.KitLane] = DoorReading.Failed("lane closed");

        var (state, reason) = PartsReport.Status(Part.WeaponCommands, r);

        Assert.Equal(DoorState.Closed, state);
        Assert.Equal("lane closed", reason);

        r[Door.Roster] = DoorReading.Failed("roster closed");
        Assert.Equal("roster closed", PartsReport.Status(Part.WeaponCommands, r).Reason);
    }

    [Fact]
    public void IsOn_is_true_only_for_open()
    {
        var r = AllOpen();
        r[Door.Sensing] = DoorReading.Waiting("w");
        Assert.False(PartsReport.IsOn(Part.Sensing, r));
        Assert.True(PartsReport.IsOn(Part.KillCounting, r));
    }

    [Fact]
    public void Box_lists_every_part_once_and_the_groups_hold_the_right_parts()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("party data moved");
        r[Door.PromptHook] = DoorReading.Waiting("no hooks yet");

        string box = PartsReport.ComposeBox(r, "", "");

        foreach (var label in Labels)
            Assert.Equal(box.IndexOf(label, StringComparison.Ordinal), box.LastIndexOf(label, StringComparison.Ordinal));
        foreach (var label in Labels)
            Assert.True(box.IndexOf(label, StringComparison.Ordinal) >= 0, label + " is missing from the box");
        int still = box.IndexOf("Still working", StringComparison.Ordinal);
        int off = box.IndexOf("Switched off", StringComparison.Ordinal);
        int waiting = box.IndexOf("Still waiting", StringComparison.Ordinal);
        Assert.True(still >= 0 && off > still && waiting > off);
        Assert.InRange(box.IndexOf("battle sensing", StringComparison.Ordinal), still, off);
        Assert.InRange(box.IndexOf("extended inventory", StringComparison.Ordinal), still, off);
        Assert.InRange(box.IndexOf("kill counting", StringComparison.Ordinal), off, waiting);
        Assert.InRange(box.IndexOf("twin weapons", StringComparison.Ordinal), off, waiting);
        Assert.True(box.IndexOf("banners", waiting, StringComparison.Ordinal) > waiting);
    }

    [Fact]
    public void Box_carries_the_drill_sentence_and_the_hedge_only_when_given()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("party data moved");

        string plain = PartsReport.ComposeBox(r, "", "");
        string loud = PartsReport.ComposeBox(r, PartsReport.RosterDrillSentence, LaunchGuard.UnknownBuildHedge);

        Assert.DoesNotContain("LW_FORCE_", plain);
        Assert.DoesNotContain("the cause may be the game update itself", plain);
        Assert.Contains("LW_FORCE_ROSTER_MISMATCH", loud);
        Assert.Contains("the cause may be the game update itself", loud);
    }

    [Fact]
    public void Box_keeps_the_owner_copy_and_never_speaks_the_scanner_stand_down_phrase()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("party data moved");

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains("switched off some of its parts", box);
        Assert.Contains("attach your logs", box);
        Assert.Contains("ptyrawl@gmail.com", box);
        Assert.Contains("livingweapon.log", box);
        Assert.DoesNotContain("standing down to protect your save", box);
        Assert.DoesNotContain("switched itself off", box);
    }

    [Fact]
    public void The_box_leaves_out_a_group_that_has_no_parts()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains("Still working:", box);
        Assert.Contains("Switched off:", box);
        Assert.DoesNotContain("Still waiting:", box);
        Assert.DoesNotContain(": nothing", box);
    }

    [Fact]
    public void The_box_leaves_out_the_working_group_when_every_part_is_off_or_waiting()
    {
        var r = new Dictionary<Door, DoorReading>
        {
            [Door.Roster] = DoorReading.Failed("x"),
            [Door.KitLane] = DoorReading.Failed("x"),
            [Door.Sensing] = DoorReading.Failed("x"),
            [Door.PromptHook] = DoorReading.Failed("x"),
            [Door.ExtendedArm] = DoorReading.Failed("x"),
            [Door.TwinPistols] = DoorReading.Failed("x"),
        };

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.DoesNotContain("Still working:", box);
        Assert.DoesNotContain(": nothing", box);
    }
    [Fact]
    public void Box_lists_working_parts_without_their_notes()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("party data moved");

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains("Still working: battle sensing, extended inventory; banners (quiet while kill counting is off).", box);
        Assert.DoesNotContain("nothing to announce", box);
    }

    [Fact]
    public void Box_collapses_a_waiting_group_that_shares_one_reason()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Waiting("waiting for a save to load");
        r[Door.KitLane] = DoorReading.Failed("job command data rewritten");

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains("Still waiting: the other 6 parts (waiting for a save to load).", box);
        Assert.DoesNotContain("Still waiting: kill counting", box);
    }

    [Fact]
    public void Box_keeps_the_waiting_list_when_reasons_differ()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Waiting("waiting for a save to load");
        r[Door.PromptHook] = DoorReading.Waiting("the loader has not handed over its hooks yet");
        r[Door.KitLane] = DoorReading.Failed("job command data rewritten");

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains("banners (the loader has not handed over its hooks yet)", box);
        Assert.Contains("kill counting, stat growth", box);
        Assert.DoesNotContain("the other", box);
    }

    [Fact]
    public void Box_names_a_small_waiting_group_even_when_it_shares_one_reason()
    {
        var r = AllOpen();
        r[Door.PromptHook] = DoorReading.Waiting("the loader has not handed over its hooks yet");
        r[Door.ExtendedArm] = DoorReading.Waiting("the loader has not handed over its hooks yet");
        r[Door.KitLane] = DoorReading.Failed("job command data rewritten");

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains("Still waiting: banners, extended inventory (the loader has not handed over its hooks yet).", box);
        Assert.DoesNotContain("the other", box);
    }

    [Fact]
    public void Twin_door_closed_alone_switches_off_only_twin_weapons()
    {
        var r = AllOpen();
        r[Door.TwinPistols] = DoorReading.Failed("the menu flags the twin weapon grant relies on do not read as expected");

        var off = Enum.GetValues<Part>().Where(p => PartsReport.Status(p, r).State == DoorState.Closed).ToArray();

        Assert.Equal(new[] { Part.TwinPistols }, off);
        Assert.Equal("the menu flags the twin weapon grant relies on do not read as expected",
            PartsReport.Status(Part.TwinPistols, r).Reason);
        Assert.True(PartsReport.IsOn(Part.KillCounting, r));
        string line = PartsReport.ComposeLine(r);
        Assert.Contains("twin weapons OFF (the menu flags", line);
        Assert.Contains("kill counting ON", line);
        Assert.Equal(9, line.Split("ON").Length - 1);
    }

    [Fact]
    public void Twin_door_waiting_with_the_roster_open_reads_twin_weapons_waiting_with_the_twin_reason()
    {
        var r = AllOpen();
        r[Door.TwinPistols] = DoorReading.Waiting("the menu flags cannot be read yet");

        var (state, reason) = PartsReport.Status(Part.TwinPistols, r);

        Assert.Equal(DoorState.Waiting, state);
        Assert.Equal("the menu flags cannot be read yet", reason);
        Assert.Contains("twin weapons WAITING (the menu flags cannot be read yet)", PartsReport.ComposeLine(r));
        Assert.True(PartsReport.IsOn(Part.KillCounting, r));
    }

    [Fact]
    public void Drill_sentence_joins_the_roster_sentence_first_and_the_twin_sentence_second()
    {
        Assert.Equal("", PartsReport.DrillSentence(false, false));
        Assert.Equal(PartsReport.RosterDrillSentence, PartsReport.DrillSentence(true, false));
        Assert.Equal(PartsReport.TwinDrillSentence, PartsReport.DrillSentence(false, true));
        Assert.Equal(PartsReport.RosterDrillSentence + " " + PartsReport.TwinDrillSentence,
            PartsReport.DrillSentence(true, true));
        Assert.Contains("LW_FORCE_TWIN_MISMATCH", PartsReport.TwinDrillSentence);
        Assert.DoesNotContain("LW_FORCE_ROSTER_MISMATCH", PartsReport.TwinDrillSentence);
    }
}
