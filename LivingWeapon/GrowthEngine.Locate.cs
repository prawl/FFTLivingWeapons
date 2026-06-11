using System.Collections.Generic;

namespace LivingWeapon;

internal sealed partial class GrowthEngine
{
    /// <summary>Find (and cache) this player's combat struct by fingerprint; guarded reads. The
    /// struct's weapon field (+0x20) holds ONE of the wielded hands (the right hand for a
    /// dual-wielder), so we match it against either hand.
    ///
    /// Scan order: player-side slots (n >= 0) first, then enemy-side (n &lt; 0), so a player
    /// is located before an enemy is even considered. Homogeneous twin tie-break: when all
    /// matching entries share the same weapon id (the unit literally appears twice in the static
    /// array because a battle restart freezes the old slot), prefer an entry with a real (non-zero)
    /// grid position; if all share the same position too, return the first deterministically.
    /// Only HETEROGENEOUS multi-matches (different weapon ids) refuse. The ambiguous-match log is
    /// throttled to once per battle per slot so a refused slot doesn't spam every ~100ms tick.</summary>
    private long LocateStruct(int slot, int level, int brave, int faith, List<(int weapon, WeaponMeta m)> hands)
    {
        if (_structForSlot.TryGetValue(slot, out long cached) && MatchesEntry(cached, level, brave, faith, hands))
            return cached;
        // Build scan order: player-side (n=0..+24) before enemy-side (n=-1..-24).
        long found = 0; int matchCount = 0; int tieWeapon = -1; bool homogeneous = true;
        for (int pass = 0; pass < 2 && matchCount == 0; pass++)
        {
            int lo = pass == 0 ? 0 : -Offsets.CombatSearchSlots;
            int hi = pass == 0 ? Offsets.CombatSearchSlots : -1;
            for (int n = lo; n <= hi; n++)
            {
                long s = Offsets.CombatAnchor + (long)n * Offsets.CombatStride;
                if (!MatchesEntry(s, level, brave, faith, hands)) continue;
                int wid = Mem.U16(s + Offsets.CWeapon);
                if (tieWeapon == -1) tieWeapon = wid;
                else if (tieWeapon != wid) homogeneous = false;
                matchCount++; found = s;
            }
        }
        if (matchCount == 1)
        {
            _structForSlot[slot] = found;
            if (!_logged) { _logged = true; Log.Info($"growth: found combat struct for party slot {slot}"); }
            return found;
        }
        if (matchCount > 1)
        {
            if (homogeneous)
            {
                long pick = PickHomogeneousTwin(level, brave, faith, hands);
                if (pick != 0)
                {
                    _structForSlot[slot] = pick;
                    if (!_logged) { _logged = true; Log.Info($"growth: found combat struct for party slot {slot}"); }
                    return pick;
                }
            }
            if (_ambiguousLogged.Add(slot))
                Log.Info($"growth: ambiguous struct locate for party slot {slot} ({matchCount} matches) -- skipping");
        }
        return 0;
    }

    /// <summary>Re-scan for a homogeneous multi-match (all matches share the same weapon id) and
    /// apply the twin tie-break: prefer a real-position (non-zero gx or gy) entry; if all are at
    /// (0,0) return the first. Returns 0 when the scan produces no matches at all (shouldn't happen
    /// at the call site, but guard against an empty pass).</summary>
    private static long PickHomogeneousTwin(int level, int brave, int faith, List<(int weapon, WeaponMeta m)> hands)
    {
        long first = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int lo = pass == 0 ? 0 : -Offsets.CombatSearchSlots;
            int hi = pass == 0 ? Offsets.CombatSearchSlots : -1;
            for (int n = lo; n <= hi; n++)
            {
                long s = Offsets.CombatAnchor + (long)n * Offsets.CombatStride;
                if (!MatchesEntry(s, level, brave, faith, hands)) continue;
                if (first == 0) first = s;
                bool realPos = Mem.U8(s + Offsets.AGx) != 0 || Mem.U8(s + Offsets.AGy) != 0;
                if (realPos) return s;
            }
        }
        return first;   // all at (0,0): return first deterministically
    }

    /// <summary>True if S is a readable combat struct matching this unit (level + brave/faith + its
    /// weapon field equals either wielded hand + sane PA/MA). Internal so tests can drive it directly
    /// via GCHandle-pinned buffers (Mem RPM/WPM work on any committed address in the process).</summary>
    internal static bool MatchesEntry(long s, int level, int brave, int faith, List<(int weapon, WeaponMeta m)> hands)
    {
        if (!Mem.Readable(s, StructSpan)) return false;
        int cw = Mem.U16(s + Offsets.CWeapon);
        if (!hands.Exists(x => x.weapon == cw)) return false;
        if (!Band.LevelMatchesRoster(level, Mem.U8(s + Offsets.CLevel))) return false;   // level = pre-battle roster value
        if (Mem.U8(s + Offsets.CBrave) != brave || Mem.U8(s + Offsets.CFaith) != faith) return false;
        int pa = Mem.U8(s + Offsets.CPa), ma = Mem.U8(s + Offsets.CMa);
        return pa >= StatMin && pa <= SigStatHi && ma >= StatMin && ma <= SigStatHi;
    }
}
