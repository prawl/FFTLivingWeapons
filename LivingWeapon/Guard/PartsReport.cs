using System.Collections.Generic;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// The pure half of the parts report. Thirteen door readings in, one status per part out, composed into the
/// single log line and the single box text. Dependency free like FingerprintGuard.
/// </summary>
internal static class PartsReport
{
    /// <summary>The scanner mark every report line starts with.</summary>
    internal const string PartsMark = "Parts report:";

    internal const string RosterDrillSentence =
        "This stand down was forced by the LW_FORCE_ROSTER_MISMATCH drill flag (dev builds only).";

    internal const string TwinDrillSentence =
        "This stand down was forced by the LW_FORCE_TWIN_MISMATCH drill flag (dev builds only).";

    /// <summary>The drill sentences for the flags that are on, roster first.</summary>
    internal static string DrillSentence(bool roster, bool twin)
        => roster && twin ? RosterDrillSentence + " " + TwinDrillSentence
         : roster ? RosterDrillSentence : twin ? TwinDrillSentence : "";

    internal const string TableDrillSentence =
        "This closure was forced by the LW_FORCE_TABLE_MISMATCH drill flag (dev builds only).";

    internal const string BandDrillSentence =
        "This stand down was forced by the LW_FORCE_BAND_MISMATCH drill flag (dev builds only).";

    internal const string SomePausedHeadline = "Living Weapons: some features are paused";
    internal const string SomePausedBody =
        "Most of the mod is running normally. A few parts found the game data in a different place than expected, "
        + "so they have paused themselves rather than risk your save.";
    internal const string AllPausedHeadline = "Living Weapons has paused itself for this session";
    internal const string AllPausedBody =
        "The mod found the game data in a different place than expected on every check, "
        + "so nothing is running rather than risk your save.";
    private const string SaveIsFine = " Your game and save are fine.";

    /// <summary>The last line of every box, so a screenshot says which mod and which game it came from.</summary>
    internal static string Footer(string version, string stamp) => $"Living Weapons {version}, game build {stamp}";

    private const string BannersNote = "connected; nothing to announce while kill counting is off";


    private readonly struct Row
    {
        public readonly Part Part;
        public readonly string Label;
        public readonly Door[] Doors;
        public Row(Part part, string label, params Door[] doors) { Part = part; Label = label; Doors = doors; }
    }

    private static readonly Row[] Rows =
    {
        new(Part.Sensing, "battle sensing", Door.Sensing),
        new(Part.KillCounting, "kill counting", Door.Roster, Door.Band, Door.StaticArray),
        new(Part.StatGrowth, "stat growth", Door.Roster, Door.Band),
        new(Part.CardPaint, "card paint", Door.Roster),
        new(Part.BandSignatures, "weapon powers in battle (Maim, Plague and the rest)", Door.Roster, Door.Band, Door.StaticArray),
        new(Part.WeaponCommands, "weapon commands", Door.Roster, Door.KitLane),
        new(Part.Banners, "banners", Door.PromptHook),
        new(Part.ExtendedInventory, "extended inventory", Door.ExtendedArm),
        new(Part.TwinPistols, "twin weapons", Door.Roster, Door.TwinPistols),
        new(Part.Bulwark, "bulwark", Door.Roster, Door.TerrainGrid),
        new(Part.WeaponColors, "weapon colors", Door.Roster, Door.PaletteBanks),
        new(Part.GunGrowth, "gun growth", Door.Roster, Door.ItemStats),
        new(Part.PoachDen, "poach den", Door.Roster, Door.PoachStore),
        new(Part.Provoke, "provoke", Door.Roster, Door.KitLane, Door.ProvokeTables),
    };

    private static Row RowOf(Part part)
    {
        foreach (var row in Rows) if (row.Part == part) return row;
        throw new KeyNotFoundException(part.ToString());
    }

    /// <summary>Closed if any door is closed (the first in door order names the reason), else Waiting if
    /// any is waiting, else Open.</summary>
    public static (DoorState State, string Reason) Status(Part part, IReadOnlyDictionary<Door, DoorReading> readings)
    {
        var row = RowOf(part);
        foreach (var door in row.Doors)
            if (readings[door].State == DoorState.Closed) return (DoorState.Closed, readings[door].Reason);
        foreach (var door in row.Doors)
            if (readings[door].State == DoorState.Waiting) return (DoorState.Waiting, readings[door].Reason);
        return (DoorState.Open, "");
    }

    internal static IReadOnlyList<Door> DoorsOf(Part part) => RowOf(part).Doors;

    /// <summary>The user facing label of a part, as the report and the box print it.</summary>
    internal static string LabelOf(Part part) => RowOf(part).Label;

    public static bool IsOn(Part part, IReadOnlyDictionary<Door, DoorReading> readings)
        => Status(part, readings).State == DoorState.Open;

    private static string Word(DoorState state) => state switch
    {
        DoorState.Open => "ON",
        DoorState.Closed => "OFF",
        _ => "WAITING",
    };

    private static string Detail(Row row, DoorState state, string reason, IReadOnlyDictionary<Door, DoorReading> readings)
    {
        if (state != DoorState.Open) return reason;
        return row.Part == Part.Banners && !IsOn(Part.KillCounting, readings) ? BannersNote : "";
    }

    public static string ComposeLine(IReadOnlyDictionary<Door, DoorReading> readings)
    {
        var sb = new StringBuilder(PartsMark);
        bool first = true;
        foreach (var row in Rows)
        {
            var (state, reason) = Status(row.Part, readings);
            string detail = Detail(row, state, reason, readings);
            sb.Append(first ? " " : "; ").Append(row.Label).Append(' ').Append(Word(state));
            if (detail.Length > 0) sb.Append(" (").Append(detail).Append(')');
            first = false;
        }
        return sb.ToString();
    }

    public static string ComposeBox(IReadOnlyDictionary<Door, DoorReading> readings, string drillSentence, string hedge, string footer)
    {
        bool anyWorking = false;
        foreach (var row in Rows) anyWorking |= Status(row.Part, readings).State == DoorState.Open;
        var sb = new StringBuilder();
        sb.Append(anyWorking ? SomePausedHeadline : AllPausedHeadline).Append("\n\n");
        sb.Append(anyWorking ? SomePausedBody : AllPausedBody).Append("\n\n");
        AppendGroup(sb, "Still working:", DoorState.Open, readings);
        AppendGroup(sb, "Paused this session:", DoorState.Closed, readings);
        AppendGroup(sb, "Still being checked (as the game loads and your first battle starts):", DoorState.Waiting, readings);
        sb.Append(PartsCopy.Hedge);
        if (hedge.Length > 0) sb.Append(' ').Append(hedge.Trim());
        sb.Append(SaveIsFine).Append("\n\n").Append(PartsCopy.WhatToDoBox);
        if (drillSentence.Length > 0) sb.Append("\n\n").Append(drillSentence);
        if (footer.Length > 0) sb.Append("\n\n").Append(footer);
        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, string heading, DoorState want, IReadOnlyDictionary<Door, DoorReading> readings)
    {
        var labels = new List<string>();
        foreach (var row in Rows)
            if (Status(row.Part, readings).State == want) labels.Add(row.Label);
        if (labels.Count == 0) return;
        sb.Append(heading).Append("\n  ").Append(string.Join(", ", labels)).Append("\n\n");
    }
}
