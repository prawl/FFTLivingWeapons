using System.Collections.Generic;
using System.Text;

namespace LivingWeapon;

/// <summary>
/// The pure half of the parts report. Ten door readings in, one status per part out, composed into the
/// single log line and the single box text. Dependency free like FingerprintGuard.
/// </summary>
internal static class PartsReport
{
    /// <summary>The scanner mark every report line starts with.</summary>
    internal const string PartsMark = "Parts report:";

    internal const string RosterDrillSentence =
        "This stand down was forced by the LW_FORCE_ROSTER_MISMATCH drill flag (dev builds only).";

    internal const string TableDrillSentence =
        "This closure was forced by the LW_FORCE_TABLE_MISMATCH drill flag (dev builds only).";

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
        new(Part.KillCounting, "kill counting", Door.Roster),
        new(Part.StatGrowth, "stat growth", Door.Roster),
        new(Part.CardPaint, "card paint", Door.Roster),
        new(Part.BandSignatures, "band signatures", Door.Roster),
        new(Part.WeaponCommands, "weapon commands", Door.Roster, Door.KitLane),
        new(Part.Banners, "banners", Door.PromptHook),
        new(Part.ExtendedInventory, "extended inventory", Door.ExtendedArm),
        new(Part.TwinPistols, "twin weapons", Door.Roster),
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

    public static string ComposeBox(IReadOnlyDictionary<Door, DoorReading> readings, string drillSentence, string hedge)
    {
        var sb = new StringBuilder();
        sb.Append("Living Weapons has switched off some of its parts\n\n");
        sb.Append("Part of what the mod needs is not where it expected, usually because the game just got an update "
            + "or another installed mod changed the same data. The parts below that failed stay off this session "
            + "to keep your save safe. Your game runs normally.\n\n");
        AppendGroup(sb, "Still working:", DoorState.Open, readings);
        AppendGroup(sb, "Switched off:", DoorState.Closed, readings);
        AppendGroup(sb, "Still waiting:", DoorState.Waiting, readings);
        sb.Append("Fix: look for a newer Living Weapons on its mod page. Still stuck? Email me at ptyrawl@gmail.com "
            + "and attach your logs (Open Folder on the mod's page in Reloaded-II, the file is livingweapon.log).");
        if (hedge.Length > 0) sb.Append("\n\n").Append(hedge.TrimStart());
        if (drillSentence.Length > 0) sb.Append("\n\n").Append(drillSentence);
        return sb.ToString();
    }

    private static void AppendGroup(StringBuilder sb, string heading, DoorState want,
        IReadOnlyDictionary<Door, DoorReading> readings)
    {
        // Parts sharing one reason are listed together and the reason is given once.
        var byDetail = new List<(string Detail, List<string> Labels)>();
        foreach (var row in Rows)
        {
            var (state, reason) = Status(row.Part, readings);
            if (state != want) continue;
            string detail = Detail(row, state, reason, readings);
            int i = byDetail.FindIndex(g => g.Detail == detail);
            if (i < 0) byDetail.Add((detail, new List<string> { row.Label }));
            else byDetail[i].Labels.Add(row.Label);
        }
        if (byDetail.Count == 0) return;
        var clauses = new List<string>();
        foreach (var (detail, labels) in byDetail)
        {
            string joined = string.Join(", ", labels);
            clauses.Add(detail.Length > 0 ? $"{joined} ({detail})" : joined);
        }
        sb.Append(heading).Append(' ').Append(string.Join("; ", clauses)).Append('.').Append("\n\n");
    }
}
