using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wielder's LW-252 ambiguous-roster seam, split out of Wielder.cs (200-line house trigger):
/// the per-row disambiguation Locate/LocateAll's public (implicit-resolve) overloads route to
/// when ResolveAnyHandNameId's any-hand scan finds 2+ roster rows sharing weaponId+fp. Pure
/// move off Wielder.cs -- see LocateAmbiguous/LocateAllAmbiguous's own doc comments for the two
/// rules, and Wielder.cs's public Locate doc comment for why the branch exists at all.
/// </summary>
internal static partial class Wielder
{
    /// <summary>LW-252's per-row disambiguation for <see cref="Locate(IGameMemory,int,IReadOnlyList{int},(int,int,int))"/>'s
    /// implicit-resolve overload: reached only when ResolveAnyHandNameId's any-hand scan found 2+
    /// roster rows sharing weaponId+fp -- the case the old shared -1 sentinel could not tell apart
    /// from "nobody at all", which let tier 2's veto go inert and hand out a foreign-nameId band
    /// collider (see the public Locate doc comment). Collects every matching row
    /// (Wielder.Roster.cs's <see cref="CollectAmbiguousRows"/>) and, when every row's own nameId
    /// reads &gt; 0, probes EACH ROW independently with <see cref="LocateTier1"/> using THAT row's
    /// own nameId and hand set -- never the internal 5-arg
    /// <see cref="Locate(IGameMemory,int,IReadOnlyList{int},(int,int,int),int)"/>, which falls
    /// through to tier 2 on zero tier-1 candidates and tier 2 passes nameId-0 entries, reopening
    /// the exact leak this branch exists to close. A row whose OWN nameId reads &lt;= 0 makes the
    /// whole probe untrustworthy (running tier 1 with rosterNameId 0 would re-open the 0==0 trap
    /// <see cref="LocateTier1"/>'s own doc warns about) -- rather than guess, ALL rows degrade to
    /// the pre-LW-252 -1 path in that case (rule (a), byte-identical to today). Otherwise (rule
    /// (b)): exactly one row resolving to a nonzero band address is the answer; two rows resolving
    /// (a genuine multi-identity tie) or zero rows resolving (nobody here is deployed) both
    /// refuse.</summary>
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

    /// <summary>LocateAll's counterpart to <see cref="LocateAmbiguous"/> (LW-252): same two rules,
    /// applied per collected row using LocateAll's OWN tier-1 predicate (mirrors the inline block
    /// in the 5-arg <see cref="LocateAll(IGameMemory,int,IReadOnlyList{int},(int,int,int),int,List{long})"/>
    /// overload in Wielder.cs, scoped to one row's nameId + hand set at a time). Rule (a): any
    /// row's own nameId &lt;= 0 degrades the whole call to the pre-LW-252 -1 path (today's plain
    /// fp scan, no veto). Rule (b): exactly one row whose tier-1 scan collects at least one band
    /// entry contributes ITS entries to <paramref name="results"/>; two contributing rows
    /// (distinct deployed wielders sharing a weapon) or zero both leave <paramref name="results"/>
    /// untouched -- LocateAll never clears its own output (existing contract), so "untouched"
    /// here means exactly that.</summary>
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
