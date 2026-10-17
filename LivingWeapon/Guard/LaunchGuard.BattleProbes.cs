using System.Collections.Generic;
using System.Linq;

namespace LivingWeapon;

/// <summary>The two reads behind the band and static array doors. A band no party row mirrors still waits when the list agrees with it.</summary>
internal sealed partial class LaunchGuard
{
    private LandmarkReading ProbeBandRegion()
    {
        var band = _stepBand;
        if (band is null) return new LandmarkReading(LandmarkVerdict.Unreadable, "page unreadable");
        var rows = ReadRosterRows();
        if (BattleRegionShape.MirrorsRoster(band, rows)) { _noMirrorBattles = 0; return LandmarkVerdict.Match; }
        if (band.Count > 0 && StepArray() is { } list && BattleRegionShape.SharesUnit(list, band))
            return NoMirrorButListAgrees(band, rows.Count);
        string detail = band.Count == 0
            ? "no unit shapes in the live battle data"
            : $"{band.Count} unit shapes, none mirrors a party row (rows checked {rows.Count})";
        return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
    }

    private LandmarkReading ProbeStaticArray()
    {
        var band = _stepBand;
        if (band is null || band.Count == 0) return new LandmarkReading(LandmarkVerdict.Unreadable, "live battle data empty");
        var tuples = StepArray();
        if (tuples is null) return new LandmarkReading(LandmarkVerdict.Unreadable, "page unreadable");
        if (BattleRegionShape.SharesUnit(tuples, band)) return LandmarkVerdict.Match;
        string detail = tuples.Count == 0
            ? "no unit shapes in the battle unit list"
            : $"{tuples.Count} list shapes share no unit with the {band.Count} live shapes";
        return new LandmarkReading(LandmarkVerdict.Mismatch, detail);
    }

    private List<(int mhp, int lvl, int br, int fa)>? StepArray()
    {
        if (_stepArrayRead) return _stepArray;
        _stepArrayRead = true;
        _stepArray = _mem.TryReadBytes(Offsets.ArrayReadBase, Offsets.NSlots * Offsets.ArrayStride, out var slots)
            ? BattleRegionShape.ParseArray(slots) : null;
        return _stepArray;
    }

    private LandmarkReading NoMirrorButListAgrees(List<BattleRegionShape.Unit> band, int rows)
    {
        if (_stepOnField && !_noMirrorCounted) { _noMirrorCounted = true; _noMirrorBattles++; }
        if (_noMirrorBattles >= NoMirrorBattleLimit)
            return new LandmarkReading(LandmarkVerdict.Mismatch,
                $"{band.Count} unit shapes, none mirrors a party row in {_noMirrorBattles} battles in a row (rows checked {rows})");
        if (!_noMirrorNoted)
        {
            _noMirrorNoted = true;
            string shapes = string.Join(", ", band.Take(16).Select(u => $"{u.NameId}:L{u.Lvl} B{u.Br} F{u.Fa} M{u.Mhp}"));
            ModLogger.Debug(LogVerb.Startup, "No unit in this battle matches a party member, but the battle unit list agrees with the live battle unit data, so that data is where this mod expects it; the check waits for a battle with the party in it. (detail: rows checked "
                + rows + "; units " + shapes + ")");
        }
        return new LandmarkReading(LandmarkVerdict.Unreadable, "no unit mirrors a party row; the list agrees");
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
