using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-398: the band door and the static array door over FakeSparseMemory. The roster row comes from
/// EngineTests.HealthyMemory (level 12, brave 70, faith 65, name id 1) and the two regions are whole
/// TerrainBlocks, because each probe reads its region in one block. A "field step" is
/// StepBattleDoors(true, true) and a "live step" is StepBattleDoors(true, false).
/// </summary>
public class BattleDoorTests
{
    private const int Debounce = 30;

    internal sealed class Rig
    {
        public readonly FakeSparseMemory Mem;
        public readonly LaunchGuard G;
        public readonly List<(string type, string payload)> Records = new();
        public readonly List<string> Flushes = new();
        public readonly byte[] Band = BattleRegionShapeTests.EmptyBand();
        public readonly byte[] Arr = BattleRegionShapeTests.EmptyArray();

        public Rig(bool drill = false, bool rosterArmed = true, int level = 12)
        {
            Mem = EngineTests.HealthyMemory(level: level);
            Mem.TerrainBlocks[Offsets.FrameReadBase] = Band;
            Mem.TerrainBlocks[Offsets.ArrayReadBase] = Arr;
            G = new LaunchGuard(Mem, forceMismatch: false, recorder: (t, p) => Records.Add((t, p)),
                requestFlush: Flushes.Add, forceBandMismatch: drill);
            if (rosterArmed) G.Step();
        }

        public void Mirror(int seat = 3, int nameId = 1)
            => BattleRegionShapeTests.PutBand(Band, seat, 12, 70, 65, 300, 300, nameId);

        public void Populate(int slot = 4) => BattleRegionShapeTests.PutArray(Arr, slot, 300, 12, 70, 65);

        public void Field(int n = 1) { for (int i = 0; i < n; i++) G.StepBattleDoors(true, true); }

        public void Live(int n = 1) { for (int i = 0; i < n; i++) G.StepBattleDoors(true, false); }

        public int Reads(long at) => Mem.TryReadBytesAt.TryGetValue(at, out int c) ? c : 0;

        public int Armed(string phrase) => Records.Count(r => r.type == "guard" && r.payload.Contains(phrase));
    }

    private static int Lines(LogCapture cap, string phrase) => cap.File.Count(l => l.Contains(phrase));

    private const string BandGraceLine = "live battle unit data read unexpected values";
    private const string ArrayGraceLine = "battle unit list read unexpected values";

    [Fact]
    public void Doors_read_waiting_for_the_first_battle_and_never_step_out_of_battle()
    {
        var rig = new Rig();
        int before = rig.Mem.TryReadBytesCount;

        for (int i = 0; i < 1000; i++) rig.G.StepBattleDoors(false, false);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Equal(GuardState.Verifying, rig.G.StaticArrayState);
        Assert.Equal(before, rig.Mem.TryReadBytesCount);
        Assert.Equal(DoorState.Waiting, rig.G.BandDoor.State);
        Assert.Equal(LaunchGuard.BattleWaitingReason, rig.G.BandDoor.Reason);
        Assert.Equal(LaunchGuard.BattleWaitingReason, rig.G.StaticArrayDoor.Reason);
    }

    [Fact]
    public void Doors_do_not_step_while_the_roster_door_is_not_open()
    {
        var rig = new Rig(rosterArmed: false);
        rig.Mirror(); rig.Populate();

        rig.Field(1000);

        Assert.Equal(0, rig.Mem.TryReadBytesCount);
        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Equal(GuardState.Verifying, rig.G.StaticArrayState);
        Assert.Equal(LaunchGuard.BattleWaitingReason, rig.G.BandDoor.Reason);
    }

    [Fact]
    public void Band_door_arms_on_the_first_live_step_through_tier_one()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Mirror(nameId: 1);

        rig.Live();
        rig.Live(5);

        Assert.Equal(GuardState.Armed, rig.G.BandState);
        Assert.Equal(DoorState.Open, rig.G.BandDoor.State);
        Assert.Equal(1, rig.Armed("band door armed (a band entry mirrors its roster row)"));
        Assert.Equal(1, Lines(cap, "The live battle unit data reads as expected; kill counting, stat growth and the band signatures are enabled."));
    }

    [Fact]
    public void Band_door_arms_through_tier_two_when_the_frame_name_id_is_zero()
    {
        var rig = new Rig();
        rig.Mirror(nameId: 0);

        rig.Live();

        Assert.Equal(DoorState.Open, rig.G.BandDoor.State);
    }

    [Fact]
    public void Band_door_waits_on_a_foreign_name_id_with_matching_stats_then_closes_after_the_grace()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Mirror(nameId: 9);

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        Assert.Contains(cap.File, l => l.Contains("none mirrors a party row (rows checked 1)"));
    }

    [Fact]
    public void Band_all_zero_on_the_field_is_held_through_the_grace_then_closes_with_a_warn()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = new Rig();

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce - 1);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Equal(1, Lines(cap, BandGraceLine));
        Assert.Empty(rig.Flushes);

        rig.Field();

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        var warn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("live battle unit data is not where this mod expects it"));
        Assert.Contains("no unit shapes in the live battle data", warn);
        Assert.DoesNotContain("standing down to protect your save", warn);
        Assert.DoesNotContain("LW_FORCE_BAND_MISMATCH", warn);
        Assert.Contains("Kill counting, stat growth and the weapon powers in battle are paused for this session", warn);
        Assert.Equal(1, rig.Armed("band stand-down (band-region (no unit shapes in the live battle data))"));
        Assert.Equal("standdown", Assert.Single(rig.Flushes));
        var door = rig.G.BandDoor;
        Assert.Equal(DoorState.Closed, door.State);
        Assert.True(door.FailedCheck);
        Assert.Equal(LaunchGuard.BandClosedReason, door.Reason);
    }

    [Fact]
    public void A_mismatch_on_a_non_field_frame_never_counts_even_past_the_grace()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();

        rig.Live(5000);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Empty(rig.Flushes);
        Assert.DoesNotContain(rig.Records, r => r.payload.Contains("stand-down"));
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));

        rig.Mirror();
        rig.Field();

        Assert.Equal(GuardState.Armed, rig.G.BandState);
    }

    [Fact]
    public void A_mismatch_on_a_non_field_frame_past_the_grace_is_still_held()
    {
        var rig = new Rig();
        rig.Field(LaunchGuard.BattleGraceSteps + 10);

        rig.Live(500);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
    }

    [Fact]
    public void A_transient_empty_band_inside_the_grace_never_counts()
    {
        var rig = new Rig();

        rig.Field(100);
        rig.Mirror();
        rig.Field();

        Assert.Equal(GuardState.Armed, rig.G.BandState);
        Assert.Empty(rig.Flushes);
    }

    [Fact]
    public void Static_array_door_arms_when_a_list_slot_shares_a_unit_with_the_band()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Mirror(); rig.Populate();

        rig.Live();

        Assert.Equal(DoorState.Open, rig.G.StaticArrayDoor.State);
        Assert.Equal(1, rig.Armed("static array door armed (the list shares a unit with the band)"));
        Assert.Equal(1, Lines(cap, "The battle unit list reads as expected; kill counting and the band signatures are enabled."));
    }

    [Fact]
    public void Static_array_door_arms_on_a_later_step_than_the_band_door()
    {
        var rig = new Rig();
        rig.Mirror();

        rig.Live(4);
        Assert.Equal(DoorState.Open, rig.G.BandDoor.State);
        Assert.Equal(GuardState.Verifying, rig.G.StaticArrayState);

        rig.Populate();
        rig.Live(2);

        Assert.Equal(DoorState.Open, rig.G.StaticArrayDoor.State);
        Assert.Equal(1, rig.Armed("band door armed"));
    }

    [Fact]
    public void Static_array_door_waits_while_the_band_is_empty_even_past_the_grace()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = new Rig();
        rig.Populate();

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce - 1);

        Assert.Equal(GuardState.Verifying, rig.G.StaticArrayState);
        Assert.Equal(0, Lines(cap, ArrayGraceLine));

        rig.Field(2000);

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        Assert.Equal(GuardState.Verifying, rig.G.StaticArrayState);
        Assert.Equal(0, Lines(cap, ArrayGraceLine));
        Assert.DoesNotContain(rig.Records, r => r.payload.StartsWith("static array stand-down"));
    }

    [Fact]
    public void Static_array_door_stops_stepping_once_the_band_door_stood_down()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Populate();
        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);
        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        int arrayReads = rig.Reads(Offsets.ArrayReadBase);
        int bandReads = rig.Reads(Offsets.FrameReadBase);

        rig.Field(200);

        Assert.Equal(arrayReads, rig.Reads(Offsets.ArrayReadBase));
        Assert.Equal(bandReads, rig.Reads(Offsets.FrameReadBase));
        var door = rig.G.StaticArrayDoor;
        Assert.Equal(DoorState.Waiting, door.State);
        Assert.Equal(LaunchGuard.ArrayFailedFirstReason, door.Reason);
        Assert.False(door.FailedCheck);
    }

    [Fact]
    public void Static_array_all_zero_with_a_populated_band_closes_after_the_grace_with_its_own_warn()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Mirror();

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);

        Assert.Equal(DoorState.Open, rig.G.BandDoor.State);
        Assert.Equal(GuardState.StoodDown, rig.G.StaticArrayState);
        var warn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("battle unit list is not where this mod expects it"));
        Assert.Contains("no unit shapes in the battle unit list", warn);
        Assert.Contains("Kill counting and the weapon powers in battle are paused for this session because the battle unit list is not where this mod expects it; stat growth and the other parts keep working.", warn);
        Assert.DoesNotContain("standing down to protect your save", warn);
        Assert.Equal(1, rig.Armed("static array stand-down (static-array (no unit shapes in the battle unit list))"));
        var door = rig.G.StaticArrayDoor;
        Assert.Equal(DoorState.Closed, door.State);
        Assert.True(door.FailedCheck);
        Assert.Equal(LaunchGuard.ArrayClosedReason, door.Reason);
    }

    [Fact]
    public void Static_array_with_units_that_share_nothing_names_both_counts()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();
        rig.Mirror();
        BattleRegionShapeTests.PutArray(rig.Arr, 4, 999, 40, 50, 50);

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);

        Assert.Contains(cap.File, l => l.Contains("1 list shapes share no unit with the 1 live shapes"));
    }

    [Fact]
    public void Band_drill_turns_a_healthy_band_into_a_held_mismatch_then_closes_naming_the_flag()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = new Rig(drill: true);
        rig.Mirror(); rig.Populate();

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce - 1);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Equal(1, Lines(cap, BandGraceLine));
        Assert.Equal(DoorState.Open, rig.G.StaticArrayDoor.State);

        rig.Field();

        Assert.Equal(GuardState.StoodDown, rig.G.BandState);
        var door = rig.G.BandDoor;
        Assert.Contains("forced by the LW_FORCE_BAND_MISMATCH drill", door.Reason);
        var warn = Assert.Single(cap.File, l => l.Contains("[WARN]") && l.Contains("live battle unit data is not where this mod expects it"));
        Assert.Contains("drill: LW_FORCE_BAND_MISMATCH", warn);
        Assert.Contains(PartsReport.BandDrillSentence, warn);
        Assert.Contains(rig.Records, r => r.payload.Contains("drill: LW_FORCE_BAND_MISMATCH"));
        Assert.Equal(DoorState.Open, rig.G.StaticArrayDoor.State);
    }

    [Fact]
    public void Band_drill_on_an_empty_band_keeps_the_empty_detail_plus_the_tag_and_never_arms_the_array()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig(drill: true);

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);

        var warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("no unit shapes in the live battle data; drill: LW_FORCE_BAND_MISMATCH", warn);
    }

    [Fact]
    public void A_real_stand_down_names_no_flag_anywhere()
    {
        using var cap = LogCapture.Start();
        var rig = new Rig();

        rig.Field(LaunchGuard.BattleGraceSteps + Debounce);

        Assert.DoesNotContain("LW_FORCE", rig.G.BandDoor.Reason);
        Assert.DoesNotContain(cap.File, l => l.Contains("LW_FORCE"));
        Assert.DoesNotContain(rig.Records, r => r.payload.Contains("LW_FORCE"));
    }

    [Fact]
    public void An_unreadable_band_page_waits_and_never_counts()
    {
        var rig = new Rig();
        rig.Mem.TerrainBlocks.Remove(Offsets.FrameReadBase);

        rig.Field(LaunchGuard.BattleGraceSteps + 200);

        Assert.Equal(GuardState.Verifying, rig.G.BandState);
        Assert.Equal(GuardState.Verifying, rig.G.StaticArrayState);
    }

    [Fact]
    public void Readings_carry_the_stable_reasons_and_only_the_closed_ones_are_failed_checks()
    {
        var rig = new Rig();
        Assert.Equal(LaunchGuard.BattleWaitingReason, rig.G.BandDoor.Reason);

        rig.Live();
        Assert.Equal(LaunchGuard.BattleCheckingReason, rig.G.BandDoor.Reason);
        Assert.Equal(LaunchGuard.BattleCheckingReason, rig.G.StaticArrayDoor.Reason);
        Assert.False(rig.G.BandDoor.FailedCheck);

        Assert.Equal("the live battle unit data is not where this mod expects it", LaunchGuard.BandClosedReason);
        Assert.Equal("the battle unit list is not where this mod expects it", LaunchGuard.ArrayClosedReason);
        Assert.Equal("the live battle unit data check failed first", LaunchGuard.ArrayFailedFirstReason);
    }

    [Fact]
    public void The_band_region_is_read_once_per_step_whichever_door_is_working()
    {
        var rig = new Rig();
        rig.Mirror();

        rig.Live();
        int afterOne = rig.Reads(Offsets.FrameReadBase);
        rig.Live(3);

        Assert.Equal(1, afterOne);
        Assert.Equal(4, rig.Reads(Offsets.FrameReadBase));
    }
}
