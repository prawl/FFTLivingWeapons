using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>The two reads of game memory behind the band and static array doors. Decisions live in LaunchGuard.BattleDoors.cs.</summary>
internal sealed partial class LaunchGuard
{
    private LandmarkReading ProbeBandRegion()
    {
        var band = _stepBand;
        if (band is null) return new LandmarkReading(LandmarkVerdict.Unreadable, "page unreadable");
        var rows = ReadRosterRows();
        if (BattleRegionShape.MirrorsRoster(band, rows)) return LandmarkVerdict.Match;
        string detail = band.Count == 0
            ? "no unit shapes in the live battle data"
            : $"{band.Count} unit shapes, none mirrors a party row (rows checked {rows.Count})";
        return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
    }

    private LandmarkReading ProbeStaticArray()
    {
        var band = _stepBand;
        if (band is null || band.Count == 0) return new LandmarkReading(LandmarkVerdict.Unreadable, "live battle data empty");
        if (!_mem.TryReadBytes(Offsets.ArrayReadBase, Offsets.NSlots * Offsets.ArrayStride, out var slots))
            return new LandmarkReading(LandmarkVerdict.Unreadable, "page unreadable");
        var tuples = BattleRegionShape.ParseArray(slots);
        if (BattleRegionShape.SharesUnit(tuples, band)) return LandmarkVerdict.Match;
        string detail = tuples.Count == 0
            ? "no unit shapes in the battle unit list"
            : $"{tuples.Count} list shapes share no unit with the {band.Count} live shapes";
        return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
    }

    // Scalar reads on purpose, the way the roster landmark and the growth engine read these rows.
    private List<(int lvl, int br, int fa, int nameId)> ReadRosterRows()
    {
        var rows = new List<(int, int, int, int)>();
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            if (!Wielder.TryOccupiedSlot(_mem, r, out long rb, out int lvl)) continue;
            rows.Add((lvl, _mem.U8(rb + Offsets.RBrave), _mem.U8(rb + Offsets.RFaith), _mem.U16(rb + Offsets.RNameId)));
        }
        return rows;
    }
}
