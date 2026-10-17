using System;
using System.Collections.Generic;
using System.Linq;
using LivingWeapon;
using Xunit;

namespace LivingWeapon.Tests;

/// <summary>
/// LW-399: the five table doors. Every door is structural, so the tests stage the game's own addressing
/// instructions (and, for the two boot-filled tables, the fill) and flip single bytes.
/// </summary>
public class TableDoorsTests
{
    private sealed class Rig
    {
        public readonly FakeSparseMemory Mem = new();
        public readonly List<(string type, string text)> Flight = new();
        public readonly List<string> Flushes = new();
        public bool RosterOpen = true;
        public string Hedge = "";
        public string Detail = "";
        public TableDoors Doors = null!;

        public void Build(bool drill = false)
            => Doors = new TableDoors(Mem, () => Hedge, () => Detail, () => RosterOpen,
                (t, x) => Flight.Add((t, x)), f => Flushes.Add(f), drill);

        public void Step(int n = 1) { for (int i = 0; i < n; i++) Doors.Step(); }
    }

    private static Rig NewRig(bool drill = false)
    {
        var rig = new Rig();
        rig.Build(drill);
        return rig;
    }

    internal static void Stage(FakeSparseMemory mem, long addr, byte[] bytes) => mem.TerrainBlocks[addr] = (byte[])bytes.Clone();

    internal static void Flip(FakeSparseMemory mem, long addr) => mem.TerrainBlocks[addr][0] ^= 0xFF;

    internal static void StageTerrain(FakeSparseMemory m)
    {
        Stage(m, TableSignatures.TerrainVetoTestSite, TableSignatures.TerrainVetoTestBytes);
        Stage(m, TableSignatures.TerrainInspectReadSite, TableSignatures.TerrainInspectReadBytes);
    }

    internal static void StagePalette(FakeSparseMemory m)
    {
        Stage(m, TableSignatures.PaletteClearSite, TableSignatures.PaletteClearBytes);
        Stage(m, TableSignatures.PaletteBankBSite, TableSignatures.PaletteBankBBytes);
    }

    internal static void StagePoach(FakeSparseMemory m)
    {
        Stage(m, TableSignatures.PoachStoreSiteA, TableSignatures.PoachStoreBytesA);
        Stage(m, TableSignatures.PoachStoreSiteB, TableSignatures.PoachStoreBytesB);
    }

    private static long RowAddr(int row) => Offsets.LiveActionTable + row * 20L;

    /// <summary>Rows 188..190 of the live action table as the file ships them, markers and zeros.</summary>
    internal static void StageActionTemplate(FakeSparseMemory m)
    {
        var block = new byte[60];
        for (int r = 0; r < 3; r++) for (int i = 0; i < 4; i++) block[r * 20 + i] = 0xFF;
        m.TerrainBlocks[RowAddr(188)] = block;
    }

    private static void SetRow189(FakeSparseMemory m, int offset, byte value)
        => m.TerrainBlocks[RowAddr(188)][20 + offset] = value;

    private static void FillRow189Like(FakeSparseMemory m, byte[] bytes14)
    {
        for (int i = 0; i < 14; i++) SetRow189(m, 4 + i, bytes14[i]);
    }

    private static readonly byte[] DecoyFill =
        { 0x05, 0x00, 0x00, 0x01, 0x11, 0x12, 0x00, 0x00, 0x38, 0x00, 0x00, 0x2D, 0x00, 0x00 };

    internal static void StageProvoke(FakeSparseMemory m, bool filled)
    {
        Stage(m, TableSignatures.InflictTableSite, TableSignatures.InflictTableBytes);
        StageActionTemplate(m);
        if (filled) FillRow189Like(m, DecoyFill);
    }

    internal static void StageItemRows(FakeSparseMemory m, Func<int, byte[]> rowFor)
    {
        for (int i = 0; i < TableSignatures.PinnedItemIds.Length; i++)
            m.TerrainBlocks[Offsets.ItemStatsBase + TableSignatures.PinnedItemIds[i] * 8L] = rowFor(i);
    }

    private static byte[] Vanilla(int i) => (byte[])TableSignatures.PinnedItemVanillaRows[i].Clone();

    private static byte[] Loaded(int i)
    {
        var row = Vanilla(i);
        row[4] = (byte)(row[4] + 5);
        return row;
    }

    internal static void StageItems(FakeSparseMemory m, bool filled)
    {
        Stage(m, TableSignatures.ItemStatsSite, TableSignatures.ItemStatsBytes);
        StageItemRows(m, filled ? Loaded : Vanilla);
    }

    internal static void StageAll(FakeSparseMemory m)
    {
        StageTerrain(m);
        StagePalette(m);
        StagePoach(m);
        StageProvoke(m, filled: true);
        StageItems(m, filled: true);
    }

    // ---- the three plain code site doors ----

    public static IEnumerable<object[]> CodeSiteDoors() => new[]
    {
        new object[] { "terrain", TableSignatures.TerrainVetoTestSite, "the terrain data check has not run yet", "the terrain data is not where this mod expects it" },
        new object[] { "palette", TableSignatures.PaletteClearSite, "the weapon palette check has not run yet", "the weapon palette is not where this mod expects it" },
        new object[] { "poach", TableSignatures.PoachStoreSiteA, "the Poacher's Den store check has not run yet", "the Poacher's Den store is not where this mod expects it" },
    };

    internal static void StageDoor(FakeSparseMemory m, string which)
    {
        if (which == "terrain") StageTerrain(m);
        else if (which == "palette") StagePalette(m);
        else StagePoach(m);
    }

    private static DoorReading Read(TableDoors d, string which) => which switch
    {
        "terrain" => d.TerrainGrid,
        "palette" => d.PaletteBanks,
        "poach" => d.PoachStore,
        "provoke" => d.ProvokeTables,
        "item" => d.ItemStats,
        _ => throw new ArgumentOutOfRangeException(nameof(which)),
    };

    [Theory]
    [MemberData(nameof(CodeSiteDoors))]
    public void A_code_site_door_opens_on_vanilla_bytes_after_one_step(string which, long firstSite, string waiting, string closed)
    {
        var rig = NewRig();
        StageDoor(rig.Mem, which);
        rig.Step();
        Assert.Equal(DoorState.Open, Read(rig.Doors, which).State);
    }

    [Theory]
    [MemberData(nameof(CodeSiteDoors))]
    public void A_code_site_door_with_nothing_staged_waits_with_its_reason_and_no_warning(string which, long firstSite, string waiting, string closed)
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = NewRig();
        rig.Step(100);
        var r = Read(rig.Doors, which);
        Assert.Equal(DoorState.Waiting, r.State);
        Assert.Equal(waiting, r.Reason);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));
        Assert.DoesNotContain(rig.Flight, f => f.text.StartsWith("table door stand-down"));
    }

    [Theory]
    [MemberData(nameof(CodeSiteDoors))]
    public void A_flipped_code_byte_closes_the_door_after_thirty_steps_with_one_warning_and_a_flight_record(string which, long firstSite, string waiting, string closed)
    {
        using var cap = LogCapture.Start();
        var rig = NewRig();
        rig.Hedge = LaunchGuard.UnknownBuildHedge;
        StageDoor(rig.Mem, which);
        Flip(rig.Mem, firstSite);

        rig.Step(29);
        Assert.Equal(DoorState.Waiting, Read(rig.Doors, which).State);   // the debounce
        rig.Step(1);

        var r = Read(rig.Doors, which);
        Assert.Equal(DoorState.Closed, r.State);
        Assert.True(r.FailedCheck);
        Assert.Equal(closed, r.Reason);
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("expected", warn);
        Assert.Contains("observed", warn);
        int hedge = warn.IndexOf(PartsCopy.Hedge, StringComparison.Ordinal);
        int build = warn.IndexOf(LaunchGuard.UnknownBuildHedge, StringComparison.Ordinal);
        int what = warn.IndexOf(PartsCopy.WhatToDo, StringComparison.Ordinal);
        int detail = warn.IndexOf("(detail:", StringComparison.Ordinal);
        Assert.True(hedge >= 0 && hedge < build && build < what && what < detail);
        Assert.DoesNotContain("standing down to protect your save", warn);
        Assert.Contains(rig.Flight, f => f.type == "guard" && f.text.StartsWith("table door stand-down"));
        Assert.Contains("standdown", rig.Flushes);
    }

    [Fact]
    public void An_open_door_records_its_armed_edge_once()
    {
        var rig = NewRig();
        StageTerrain(rig.Mem);
        rig.Step(5);
        Assert.Single(rig.Flight, f => f.type == "guard" && f.text.StartsWith("table door armed"));
    }

    [Fact]
    public void A_closed_door_is_terminal_and_never_reverifies()
    {
        var rig = NewRig();
        StagePalette(rig.Mem);
        Flip(rig.Mem, TableSignatures.PaletteBankBSite);
        rig.Step(30);
        Assert.Equal(DoorState.Closed, rig.Doors.PaletteBanks.State);
        Flip(rig.Mem, TableSignatures.PaletteBankBSite);   // healed
        rig.Step(10);
        Assert.Equal(DoorState.Closed, rig.Doors.PaletteBanks.State);
    }

    [Fact]
    public void The_five_doors_are_independent()
    {
        var rig = NewRig();
        StageAll(rig.Mem);
        Flip(rig.Mem, TableSignatures.PoachStoreSiteB);
        rig.Step(30);
        Assert.Equal(DoorState.Closed, rig.Doors.PoachStore.State);
        Assert.Equal(DoorState.Open, rig.Doors.TerrainGrid.State);
        Assert.Equal(DoorState.Open, rig.Doors.PaletteBanks.State);
        Assert.Equal(DoorState.Open, rig.Doors.ProvokeTables.State);
        Assert.Equal(DoorState.Open, rig.Doors.ItemStats.State);
    }

    // ---- ProvokeTables ----

    [Fact]
    public void Provoke_tables_wait_on_the_pre_init_template_with_the_ability_data_reason()
    {
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: false);
        rig.Step(10);
        var r = rig.Doors.ProvokeTables;
        Assert.Equal(DoorState.Waiting, r.State);
        Assert.Equal("waiting for the game to load its ability data", r.Reason);
    }

    [Fact]
    public void Provoke_tables_open_once_the_fill_is_the_decoy_bytes()
    {
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: true);
        rig.Step();
        Assert.Equal(DoorState.Open, rig.Doors.ProvokeTables.State);
    }

    [Fact]
    public void Provoke_tables_stay_open_for_a_retuned_ability_row()
    {
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: false);
        FillRow189Like(rig.Mem, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E });
        rig.Step();
        Assert.Equal(DoorState.Open, rig.Doors.ProvokeTables.State);
    }

    [Fact]
    public void Provoke_tables_ignore_the_inflict_byte_the_mod_itself_writes()
    {
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: false);
        SetRow189(rig.Mem, 15, 0x1D);   // Provoke's own repoint value, everything else still zero
        rig.Step(10);
        Assert.Equal(DoorState.Waiting, rig.Doors.ProvokeTables.State);
    }

    [Fact]
    public void A_flipped_row_marker_closes_provoke_tables_and_names_the_row()
    {
        using var cap = LogCapture.Start();
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: true);
        rig.Mem.TerrainBlocks[RowAddr(188)][40 + 3] = 0x00;   // row 190 marker byte 3
        rig.Step(30);
        var r = rig.Doors.ProvokeTables;
        Assert.Equal(DoorState.Closed, r.State);
        Assert.True(r.FailedCheck);
        Assert.Equal("the ability data is not where this mod expects it", r.Reason);
        Assert.Contains("row 190", Assert.Single(cap.File, l => l.Contains("[WARN]")));
    }

    [Fact]
    public void A_flipped_inflict_site_closes_provoke_tables()
    {
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: true);
        Flip(rig.Mem, TableSignatures.InflictTableSite);
        rig.Step(30);
        Assert.Equal(DoorState.Closed, rig.Doors.ProvokeTables.State);
    }

    [Fact]
    public void A_flipped_inflict_site_closes_even_while_the_fill_is_still_the_template()
    {
        var rig = NewRig();
        StageProvoke(rig.Mem, filled: false);
        Flip(rig.Mem, TableSignatures.InflictTableSite);
        rig.Step(30);
        Assert.Equal(DoorState.Closed, rig.Doors.ProvokeTables.State);
    }

    // ---- ItemStats ----

    [Fact]
    public void Item_stats_wait_while_the_eight_pinned_rows_read_vanilla()
    {
        var rig = NewRig();
        StageItems(rig.Mem, filled: false);
        rig.Step(10);
        Assert.Equal(DoorState.Waiting, rig.Doors.ItemStats.State);
        Assert.Equal("waiting for the game to load its item data", rig.Doors.ItemStats.Reason);
    }

    [Fact]
    public void Item_stats_open_when_the_rows_carry_the_proposed_wp()
    {
        var rig = NewRig();
        StageItems(rig.Mem, filled: false);
        StageItemRows(rig.Mem, i => { var r = Vanilla(i); r[4] = (byte)new[] { 13, 8, 9, 7, 20, 12, 6, 4 }[i]; return r; });
        rig.Step();
        Assert.Equal(DoorState.Open, rig.Doors.ItemStats.State);
    }

    [Fact]
    public void Item_stats_open_for_a_rebalance_mod_that_matches_neither_vanilla_nor_proposed()
    {
        var rig = NewRig();
        StageItems(rig.Mem, filled: false);
        StageItemRows(rig.Mem, i => { var r = Vanilla(i); r[4] = 99; return r; });
        rig.Step();
        Assert.Equal(DoorState.Open, rig.Doors.ItemStats.State);
    }

    [Fact]
    public void Item_stats_open_on_a_partial_fill()
    {
        var rig = NewRig();
        StageItems(rig.Mem, filled: false);
        rig.Mem.TerrainBlocks[Offsets.ItemStatsBase + TableSignatures.PinnedItemIds[3] * 8L] = Loaded(3);
        rig.Step();
        Assert.Equal(DoorState.Open, rig.Doors.ItemStats.State);
    }

    [Fact]
    public void A_flipped_item_site_closes_the_door_regardless_of_the_rows()
    {
        var rig = NewRig();
        StageItems(rig.Mem, filled: true);
        Flip(rig.Mem, TableSignatures.ItemStatsSite);
        rig.Step(30);
        Assert.Equal(DoorState.Closed, rig.Doors.ItemStats.State);
        Assert.Equal("the item data is not where this mod expects it", rig.Doors.ItemStats.Reason);
    }

    [Fact]
    public void An_unreadable_item_row_waits_without_a_warning()
    {
        using var cap = LogCapture.Start(LogLevel.Debug);
        var rig = NewRig();
        StageItems(rig.Mem, filled: true);
        rig.Mem.TerrainBlocks.Remove(Offsets.ItemStatsBase + TableSignatures.PinnedItemIds[5] * 8L);
        rig.Step(50);
        Assert.Equal(DoorState.Waiting, rig.Doors.ItemStats.State);
        Assert.DoesNotContain(cap.File, l => l.Contains("[WARN]"));
    }

    // ---- stall watchers ----

    private static Rig WaitingFills(bool rosterOpen)
    {
        var rig = NewRig();
        rig.RosterOpen = rosterOpen;
        StageProvoke(rig.Mem, filled: false);
        StageItems(rig.Mem, filled: false);
        return rig;
    }

    [Fact]
    public void The_fill_watchers_stay_quiet_while_the_roster_door_is_closed()
    {
        using var cap = LogCapture.Start();
        var rig = WaitingFills(rosterOpen: false);
        rig.Step(2000);
        Assert.DoesNotContain(cap.File, l => l.Contains("about a minute"));
        Assert.DoesNotContain(rig.Flight, f => f.text.StartsWith("stalled"));
    }

    [Fact]
    public void The_fill_watchers_speak_once_at_step_1800_when_the_roster_door_is_open()
    {
        using var cap = LogCapture.Start();
        var rig = WaitingFills(rosterOpen: true);
        rig.Step(1799);
        Assert.DoesNotContain(cap.File, l => l.Contains("about a minute"));
        rig.Step(1);
        Assert.Single(cap.File, l => l.Contains("ability data check has not seen"));
        Assert.Single(cap.File, l => l.Contains("item data check still reads"));
        rig.Step(200);
        Assert.Single(cap.File, l => l.Contains("ability data check has not seen"));
        Assert.Single(cap.File, l => l.Contains("item data check still reads"));
        Assert.Single(rig.Flight, f => f.text == "stalled (ability data check unreadable for about a minute)");
        Assert.Single(rig.Flight, f => f.text == "stalled (item data check unreadable for about a minute)");
    }

    [Fact]
    public void A_closed_roster_door_breaks_the_stall_streak()
    {
        using var cap = LogCapture.Start();
        var rig = WaitingFills(rosterOpen: false);
        rig.Step(1000);
        rig.RosterOpen = true;
        rig.Step(1799);
        Assert.DoesNotContain(cap.File, l => l.Contains("about a minute"));
        rig.Step(1);
        Assert.Single(cap.File, l => l.Contains("ability data check has not seen"));
    }

    // ---- drill ----

    [Fact]
    public void The_drill_closes_the_palette_door_with_no_expected_bytes_when_nothing_really_differs()
    {
        using var cap = LogCapture.Start();
        var rig = NewRig(drill: true);
        StageAll(rig.Mem);
        rig.Step(30);

        var r = rig.Doors.PaletteBanks;
        Assert.Equal(DoorState.Closed, r.State);
        Assert.True(r.FailedCheck);
        Assert.EndsWith(", forced by the LW_FORCE_TABLE_MISMATCH drill", r.Reason);
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("palette-clear-site (drill: LW_FORCE_TABLE_MISMATCH)", warn);
        Assert.DoesNotContain("expected", warn);
        Assert.Equal(DoorState.Open, rig.Doors.TerrainGrid.State);
        Assert.Equal(DoorState.Open, rig.Doors.PoachStore.State);
        Assert.Equal(DoorState.Open, rig.Doors.ProvokeTables.State);
        Assert.Equal(DoorState.Open, rig.Doors.ItemStats.State);
    }

    [Fact]
    public void The_drill_keeps_the_real_evidence_when_the_palette_byte_really_differs()
    {
        using var cap = LogCapture.Start();
        var rig = NewRig(drill: true);
        StageAll(rig.Mem);
        Flip(rig.Mem, TableSignatures.PaletteClearSite);
        rig.Step(30);
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("expected", warn);
        Assert.Contains("observed", warn);
        Assert.Contains("; drill: LW_FORCE_TABLE_MISMATCH", warn);
    }

    [Fact]
    public void Without_the_drill_flag_the_palette_door_opens()
    {
        var rig = NewRig(drill: false);
        StageAll(rig.Mem);
        rig.Step();
        Assert.Equal(DoorState.Open, rig.Doors.PaletteBanks.State);
    }

    [Fact]
    public void The_drill_decorator_passes_an_unreadable_site_through()
    {
        var rig = NewRig(drill: true);   // nothing staged
        rig.Step(100);
        Assert.Equal(DoorState.Waiting, rig.Doors.PaletteBanks.State);
    }

    // ---- readings ----

    [Fact]
    public void Every_closed_table_reading_is_a_failed_check_and_every_state_has_one_stable_reason()
    {
        var rig = NewRig();
        StageAll(rig.Mem);
        foreach (long a in new[] { TableSignatures.TerrainVetoTestSite, TableSignatures.PaletteClearSite, TableSignatures.PoachStoreSiteA,
                     TableSignatures.InflictTableSite, TableSignatures.ItemStatsSite })
            Flip(rig.Mem, a);
        rig.Step(30);
        foreach (string d in new[] { "terrain", "palette", "poach", "provoke", "item" })
        {
            var r = Read(rig.Doors, d);
            Assert.Equal(DoorState.Closed, r.State);
            Assert.True(r.FailedCheck);
        }
        rig.Step(10);
        Assert.Equal("the ability data is not where this mod expects it", rig.Doors.ProvokeTables.Reason);
        Assert.Equal("the item data is not where this mod expects it", rig.Doors.ItemStats.Reason);
    }

    [Fact]
    public void Terrain_door_diag_names_the_expected_and_observed_hex()
    {
        using var cap = LogCapture.Start();
        var rig = NewRig();
        StageTerrain(rig.Mem);
        Flip(rig.Mem, TableSignatures.TerrainInspectReadSite);
        rig.Step(30);
        string warn = Assert.Single(cap.File, l => l.Contains("[WARN]"));
        Assert.Contains("terrain-inspect-read-site", warn);
        Assert.Contains(BitConverter.ToString(TableSignatures.TerrainInspectReadBytes), warn);
    }
}
