using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-393: the pure parts report. Thirteen door readings in, fourteen parts out with ON, OFF or WAITING and a
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
        [Door.TerrainGrid] = DoorReading.Open(),
        [Door.PaletteBanks] = DoorReading.Open(),
        [Door.PoachStore] = DoorReading.Open(),
        [Door.ProvokeTables] = DoorReading.Open(),
        [Door.ItemStats] = DoorReading.Open(),
        [Door.Band] = DoorReading.Open(),
        [Door.StaticArray] = DoorReading.Open(),
    };

    private static readonly string[] Labels =
    {
        "battle sensing", "kill counting", "stat growth", "card paint", "weapon powers in battle (Maim, Plague and the rest)",
        "weapon commands", "banners", "extended inventory", "twin weapons",
        "bulwark", "weapon colors", "gun growth", "poach den", "provoke",
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
        Assert.Equal(14, line.Split("ON").Length - 1);
    }

    [Fact]
    public void Roster_closed_turns_the_eleven_roster_parts_off_and_keeps_the_rest_on()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("the party data is not where this mod expects it");

        string[] off = { "kill counting", "stat growth", "card paint", "weapon powers in battle (Maim, Plague and the rest)",
                         "weapon commands", "twin weapons", "bulwark", "weapon colors", "gun growth", "poach den", "provoke" };
        foreach (var part in new[] { Part.KillCounting, Part.StatGrowth, Part.CardPaint, Part.BandSignatures,
                                     Part.WeaponCommands, Part.TwinPistols, Part.Bulwark, Part.WeaponColors,
                                     Part.GunGrowth, Part.PoachDen, Part.Provoke })
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
    public void Kit_lane_closed_alone_turns_only_the_two_kit_lane_parts_off()
    {
        var r = AllOpen();
        r[Door.KitLane] = DoorReading.Failed("another mod rewrote the job command data");

        string line = PartsReport.ComposeLine(r);

        Assert.Contains("weapon commands OFF (another mod rewrote the job command data)", line);
        Assert.Contains("provoke OFF (another mod rewrote the job command data)", line);
        Assert.Equal(2, line.Split("OFF").Length - 1);
        Assert.Equal(12, line.Split("ON").Length - 1);
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
            [Door.TerrainGrid] = DoorReading.Failed("x"),
            [Door.PaletteBanks] = DoorReading.Failed("x"),
            [Door.PoachStore] = DoorReading.Failed("x"),
            [Door.ProvokeTables] = DoorReading.Failed("x"),
            [Door.ItemStats] = DoorReading.Failed("x"),
            [Door.Band] = DoorReading.Failed("x"),
            [Door.StaticArray] = DoorReading.Failed("x"),
        };

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.DoesNotContain("Still working:", box);
        Assert.DoesNotContain(": nothing", box);
    }

    // ---- LW-398: the band and static array doors ----

    private static string L(Part part) => PartsReport.LabelOf(part);

    [Fact]
    public void Band_closed_turns_off_kill_counting_growth_and_band_signatures_and_leaves_the_rest_on()
    {
        var r = AllOpen();
        r[Door.Band] = DoorReading.Failed(LaunchGuard.BandClosedReason);

        var off = new[] { Part.KillCounting, Part.StatGrowth, Part.BandSignatures };
        foreach (var part in off)
        {
            var (state, reason) = PartsReport.Status(part, r);
            Assert.Equal(DoorState.Closed, state);
            Assert.Equal(LaunchGuard.BandClosedReason, reason);
        }
        foreach (var part in new[] { Part.Sensing, Part.CardPaint, Part.WeaponCommands, Part.Banners, Part.ExtendedInventory,
                                     Part.TwinPistols, Part.Bulwark, Part.WeaponColors, Part.GunGrowth, Part.PoachDen, Part.Provoke })
            Assert.Equal(DoorState.Open, PartsReport.Status(part, r).State);

        string line = PartsReport.ComposeLine(r);
        foreach (var part in off)
            Assert.Contains($"{L(part)} OFF ({LaunchGuard.BandClosedReason})", line);
        Assert.Contains($"{L(Part.TwinPistols)} ON", line);
        Assert.Contains($"{L(Part.CardPaint)} ON", line);
    }

    [Fact]
    public void Static_array_closed_turns_off_kill_counting_and_band_signatures_and_keeps_growth_on()
    {
        var r = AllOpen();
        r[Door.StaticArray] = DoorReading.Failed(LaunchGuard.ArrayClosedReason);

        Assert.Equal(DoorState.Closed, PartsReport.Status(Part.KillCounting, r).State);
        Assert.Equal(DoorState.Closed, PartsReport.Status(Part.BandSignatures, r).State);
        Assert.Equal(DoorState.Open, PartsReport.Status(Part.StatGrowth, r).State);
        Assert.Equal(DoorState.Open, PartsReport.Status(Part.CardPaint, r).State);
        Assert.Equal(DoorState.Open, PartsReport.Status(Part.Bulwark, r).State);
    }

    [Fact]
    public void Band_waiting_with_the_roster_open_shows_the_three_parts_waiting_with_the_first_battle_reason()
    {
        var r = AllOpen();
        r[Door.Band] = DoorReading.Waiting(LaunchGuard.BattleWaitingReason);
        r[Door.StaticArray] = DoorReading.Waiting(LaunchGuard.BattleWaitingReason);

        string line = PartsReport.ComposeLine(r);

        foreach (var part in new[] { Part.KillCounting, Part.StatGrowth, Part.BandSignatures })
            Assert.Contains($"{L(part)} WAITING (waiting for the first battle)", line);
        Assert.Contains("banners ON (connected; nothing to announce while kill counting is off)", line);
        Assert.Contains($"{L(Part.CardPaint)} ON", line);
    }

    [Fact]
    public void Roster_closed_outranks_a_waiting_band()
    {
        var r = AllOpen();
        r[Door.Roster] = DoorReading.Failed("roster closed");
        r[Door.Band] = DoorReading.Waiting(LaunchGuard.BattleWaitingReason);

        var (state, reason) = PartsReport.Status(Part.KillCounting, r);

        Assert.Equal(DoorState.Closed, state);
        Assert.Equal("roster closed", reason);
    }

    [Fact]
    public void Box_groups_the_three_waiting_parts_under_one_reason()
    {
        var r = AllOpen();
        r[Door.Band] = DoorReading.Waiting(LaunchGuard.BattleWaitingReason);
        r[Door.StaticArray] = DoorReading.Waiting(LaunchGuard.BattleWaitingReason);

        string box = PartsReport.ComposeBox(r, "", "");

        Assert.Contains($"Still waiting: kill counting, stat growth, {L(Part.BandSignatures)} (waiting for the first battle).", box);
    }

    [Fact]
    public void The_band_drill_sentence_names_its_flag_and_the_twin_label_is_twin_weapons()
    {
        Assert.Contains(DrillTrigger.BandFlagName, PartsReport.BandDrillSentence);
        Assert.Equal("twin weapons", L(Part.TwinPistols));
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

        Assert.Contains("Still waiting: the other 9 parts (waiting for a save to load).", box);
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
        Assert.Equal(13, line.Split("ON").Length - 1);
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

    // ---- LW-399: the five table parts ----

    [Fact]
    public void The_fourteen_labels_appear_in_the_table_order_by_index_with_twin_weapons_at_eight()
    {
        Assert.Equal(14, Labels.Length);
        Assert.Equal("twin weapons", Labels[8]);
        Assert.Equal(14, Enum.GetValues<Part>().Length);
        string line = PartsReport.ComposeLine(AllOpen());
        Assert.DoesNotContain("twin pistols", line);
        Assert.DoesNotContain("static table signatures", line);
    }

    [Theory]
    [InlineData("Bulwark", "TerrainGrid", "bulwark", "the terrain data is not where this mod expects it")]
    [InlineData("WeaponColors", "PaletteBanks", "weapon colors", "the weapon palette is not where this mod expects it")]
    [InlineData("GunGrowth", "ItemStats", "gun growth", "the item data is not where this mod expects it")]
    [InlineData("PoachDen", "PoachStore", "poach den", "the Poacher's Den store is not where this mod expects it")]
    [InlineData("Provoke", "ProvokeTables", "provoke", "the ability data is not where this mod expects it")]
    public void A_table_door_closed_alone_turns_only_its_part_off_with_its_reason(string partName, string doorName, string label, string reason)
    {
        var part = Enum.Parse<Part>(partName);
        var door = Enum.Parse<Door>(doorName);
        var r = AllOpen();
        r[door] = DoorReading.Failed(reason);

        Assert.Equal((DoorState.Closed, reason), PartsReport.Status(part, r));
        foreach (var other in Enum.GetValues<Part>().Where(p => p != part))
            Assert.Equal(DoorState.Open, PartsReport.Status(other, r).State);
        string line = PartsReport.ComposeLine(r);
        Assert.Contains($"{label} OFF ({reason})", line);
        Assert.Equal(1, line.Split("OFF").Length - 1);
    }

    [Fact]
    public void Provoke_closed_leaves_weapon_commands_on()
    {
        var r = AllOpen();
        r[Door.ProvokeTables] = DoorReading.Failed("the ability data is not where this mod expects it");
        Assert.Equal(DoorState.Open, PartsReport.Status(Part.WeaponCommands, r).State);
        Assert.Contains("weapon commands ON", PartsReport.ComposeLine(r));
    }

    [Fact]
    public void Provoke_waits_on_the_ability_data_reason_while_weapon_commands_is_on()
    {
        var r = AllOpen();
        r[Door.ProvokeTables] = DoorReading.Waiting("waiting for the game to load its ability data");
        Assert.Equal((DoorState.Waiting, "waiting for the game to load its ability data"), PartsReport.Status(Part.Provoke, r));
        Assert.Equal(DoorState.Open, PartsReport.Status(Part.WeaponCommands, r).State);
    }

    [Fact]
    public void The_box_groups_a_lone_closed_table_part_under_switched_off_by_itself()
    {
        var r = AllOpen();
        r[Door.PaletteBanks] = DoorReading.Failed("the weapon palette is not where this mod expects it");

        string box = PartsReport.ComposeBox(r, "", "");

        int off = box.IndexOf("Switched off:", StringComparison.Ordinal);
        Assert.True(off >= 0);
        string offLine = box.Substring(off, box.IndexOf('\n', off) - off);
        Assert.Contains("weapon colors (the weapon palette is not where this mod expects it)", offLine);
        Assert.DoesNotContain("gun growth", offLine);
        Assert.DoesNotContain("Still waiting:", box);
    }

    [Fact]
    public void The_table_drill_sentence_is_the_flag_text_and_appears_only_when_given()
    {
        Assert.Equal("This closure was forced by the LW_FORCE_TABLE_MISMATCH drill flag (dev builds only).", PartsReport.TableDrillSentence);
        var r = AllOpen();
        r[Door.PaletteBanks] = DoorReading.Failed("x");
        Assert.DoesNotContain("LW_FORCE_TABLE_MISMATCH", PartsReport.ComposeBox(r, "", ""));
        Assert.Contains("LW_FORCE_TABLE_MISMATCH", PartsReport.ComposeBox(r, PartsReport.TableDrillSentence, ""));
    }
}
