using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// LW-252 ambiguous-roster seam, split from Wielder.cs. The public Locate/LocateAll overloads route
/// here when ResolveAnyHandNameId finds 2+ roster rows sharing weaponId+fp.
/// </summary>
internal static partial class Wielder
{
    /// <summary>LW-252 per-row disambiguation for the implicit-resolve Locate overload. The old
    /// shared -1 sentinel could not tell this case from "nobody", which let tier 2's veto go inert
    /// and return a foreign-nameId collider. Each collected row is probed with
    /// <see cref="LocateTier1"/> under its own nameId and hands (the 5-arg Locate would fall through
    /// to tier 2 and reopen that leak). Rule (a), any row with nameId &lt;= 0 means the 0==0 trap
    /// in <see cref="LocateTier1"/> applies, so fall back to the pre-LW-252 -1 path. Rule (b), exactly
    /// one row resolving is the answer; two or zero refuse.</summary>
    private static long LocateAmbiguous(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                        (int lvl, int br, int fa) fp)
    {
        var rows = new List<(long rb, int nameId, List<int> hands)>();
        CollectAmbiguousRows(mem, weaponId, fp, rows);
        foreach (var row in rows)
            if (row.nameId <= 0) return Locate(mem, weaponId, hands, fp, -1);   // rule (a): untrustworthy row

        long resolved = 0;
        int rowsResolved = 0;
        foreach (var row in rows)
        {
            long hit = LocateTier1(mem, weaponId, row.hands, fp, row.nameId, out _);
            if (hit != 0) { rowsResolved++; resolved = hit; }
        }
        return rowsResolved == 1 ? resolved : 0;   // rule (b)
    }

    /// <summary>LocateAll counterpart of <see cref="LocateAmbiguous"/>, with the same two
    /// rules and LocateAll's own tier-1 predicate per row. Rule (a) falls back to the -1 path. Rule
    /// (b) appends one row's entries to <paramref name="results"/> only if exactly one row has hits.
    /// LocateAll never clears its output, so a refusal leaves it as it was.</summary>
    private static void LocateAllAmbiguous(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                           (int lvl, int br, int fa) fp, List<long> results)
    {
        var rows = new List<(long rb, int nameId, List<int> hands)>();
        CollectAmbiguousRows(mem, weaponId, fp, rows);
        foreach (var row in rows)
            if (row.nameId <= 0) { LocateAll(mem, weaponId, hands, fp, -1, results); return; }   // rule (a)

        var chosen = new List<long>();
        int rowsWithHits = 0;
        foreach (var row in rows)
        {
            var hits = new List<long>();
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long e = Band.Entry(s);
                if (!Band.IsValid(mem, e)) continue;
                if (!BasePredicate(mem, e, row.hands, fp, out _)) continue;
                if (mem.U16(e + Offsets.ANameId) != row.nameId) continue;
                hits.Add(e);
            }
            if (hits.Count > 0) { rowsWithHits++; chosen = hits; }
        }
        if (rowsWithHits == 1) results.AddRange(chosen);   // rule (b)
    }
}
