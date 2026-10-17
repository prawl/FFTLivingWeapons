using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// The pure shape reader behind the band and static array doors. A byte buffer in, the units that pass the
/// same bounds every consumer already applies out. No memory access, so tests drive it with plain arrays.
/// </summary>
internal static class BattleRegionShape
{
    internal readonly struct Unit
    {
        public int Lvl { get; }
        public int Br { get; }
        public int Fa { get; }
        public int Mhp { get; }
        public int Hp { get; }
        public int NameId { get; }
        public bool RealPos { get; }

        public Unit(int lvl, int br, int fa, int mhp, int hp, int nameId, bool realPos)
        {
            Lvl = lvl; Br = br; Fa = fa; Mhp = mhp; Hp = hp; NameId = nameId; RealPos = realPos;
        }
    }

    // Band.IsValid bounds, repeated here because that one reads through IGameMemory.
    public static List<Unit> ParseBand(byte[] frames)
    {
        var units = new List<Unit>();
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            int frame = s * Offsets.CombatStride;
            if (frame + Offsets.CombatStride > frames.Length) break;
            int e = frame + Offsets.BandEntry;
            int lvl = frames[e + Offsets.ALevel];
            int br = frames[e + Offsets.ABrave];
            int fa = frames[e + Offsets.AFaith];
            int mhp = U16(frames, e + Offsets.AMaxHp);
            int gx = frames[e + Offsets.AGx];
            int gy = frames[e + Offsets.AGy];
            if (lvl < 1 || lvl > 99 || br < 1 || br > 100 || fa < 1 || fa > 100) continue;
            if (mhp < 1 || mhp >= 2000 || gx > 30 || gy > 30) continue;
            units.Add(new Unit(lvl, br, fa, mhp, U16(frames, e + Offsets.AHp), U16(frames, e + Offsets.ANameId),
                gx != 0 || gy != 0));
        }
        return units;
    }

    // Band.Fingerprints bounds, which differ slightly from the band's (mhp 2000 passes, brave 0 passes).
    public static List<(int mhp, int lvl, int br, int fa)> ParseArray(byte[] slots)
    {
        var tuples = new List<(int, int, int, int)>();
        for (int s = 0; s < Offsets.NSlots; s++)
        {
            int e = s * Offsets.ArrayStride;
            if (e + Offsets.AMaxHp + 2 > slots.Length) break;
            int mhp = U16(slots, e + Offsets.AMaxHp);
            int lvl = slots[e + Offsets.ALevel];
            int br = slots[e + Offsets.ABrave];
            int fa = slots[e + Offsets.AFaith];
            if (mhp < 1 || mhp > 2000 || lvl < 1 || lvl > 99 || br > 100 || fa > 100) continue;
            tuples.Add((mhp, lvl, br, fa));
        }
        return tuples;
    }

    // Wielder's tier one and tier two without the weapon. A nonzero frame nameId that differs is vetoed, and a
    // roster nameId of zero matches only a zero frame nameId, which is stricter than Wielder on purpose.
    public static bool MirrorsRoster(IReadOnlyList<Unit> band,
        IReadOnlyList<(int lvl, int br, int fa, int nameId)> rows)
    {
        foreach (var row in rows)
            foreach (var unit in band)
            {
                if (!Band.LevelMatchesRoster(row.lvl, unit.Lvl)) continue;
                if (unit.Br != row.br || unit.Fa != row.fa) continue;
                if (unit.NameId == row.nameId || unit.NameId == 0) return true;
            }
        return false;
    }

    public static bool SharesUnit(IReadOnlyList<(int mhp, int lvl, int br, int fa)> array, IReadOnlyList<Unit> band)
    {
        foreach (var unit in band)
            foreach (var t in array)
                if (t.mhp == unit.Mhp && t.lvl == unit.Lvl && t.br == unit.Br && t.fa == unit.Fa) return true;
        return false;
    }

    private static int U16(byte[] b, int at) => b[at] | (b[at + 1] << 8);
}
