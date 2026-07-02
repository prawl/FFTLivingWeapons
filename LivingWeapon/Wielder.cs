using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wielder's BAND-address-space half: given a roster-resolved fingerprint and hand-id set
/// (see Wielder.Roster.cs), find that unit's LIVE band entry. Ports ExtraTurn's proven
/// Locate pair -- twin filter included -- behind IGameMemory so the walk is unit-testable
/// with the fake (the live caller passes a LiveMemory; reads stay RPM-backed and fail-safe).
/// </summary>
internal static partial class Wielder
{
    /// <summary>Weapon id inside the band-entry frame: CWeapon(0x20) - BandEntry(0x1C) = +0x04.</summary>
    private const int EntryWeapon = Offsets.CWeapon - Offsets.BandEntry;

    /// <summary>The wielder's LIVE band entry: the entry's weapon id must be one of their hands
    /// (the right hand for a dual-wielder) AND brave/faith must match the roster. TWIN FILTER
    /// (ExtraTurn-proven): a real-position match beats the frozen (0,0) roster duplicate; among
    /// survivors an exact weapon match outranks a hand match; a remaining tie returns 0 UNLESS all
    /// candidates carry the SAME identity tuple (weapon id, brave, faith) -- then they are copies
    /// of ONE unit (the unit literally stands on tile (0,0), confirmed live 2026-06-10: slots
    /// 25+28 both wid=51 br=89 fa=76 pos=(0,0)), and we return one deterministically rather than
    /// refusing. A tie between DIFFERENT identities is still a genuine ambiguity -> 0.</summary>
    public static long Locate(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                              (int lvl, int br, int fa) fp)
    {
        long match = 0, exact = 0;
        int matches = 0, exacts = 0;
        bool real = false;   // current candidates carry a real grid position
        // Parallel tracking for the identical-twin tie-break: record all surviving candidates.
        long firstMatch = 0, firstExact = 0;
        int tieWid = -1; bool tieHomogenous = true;   // all candidates share the same weapon id?
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            int wid = mem.U16(e + EntryWeapon);
            if (!Contains(hands, wid)) continue;
            if (!Band.LevelMatchesRoster(fp.lvl, mem.U8(e + Offsets.ALevel))) continue;   // fp.lvl is the pre-battle roster level
            if (mem.U8(e + Offsets.ABrave) != fp.br || mem.U8(e + Offsets.AFaith) != fp.fa) continue;
            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            if (real && !realPos) continue;                                  // (0,0) twin loses to a live match
            if (realPos && !real) { real = true; matches = 0; exacts = 0; exact = 0;   // match reset omitted: reassigned unconditionally below
                                    firstMatch = 0; firstExact = 0; tieWid = -1; tieHomogenous = true; }
            matches++; match = e; if (firstMatch == 0) firstMatch = e;
            if (wid == weaponId) { exacts++; exact = e; if (firstExact == 0) firstExact = e; }
            // Track homogeneity: all candidates must share the same weapon-id at their entry.
            if (tieWid == -1) tieWid = wid;
            else if (tieWid != wid) tieHomogenous = false;
        }
        if (exacts == 1) return exact;
        if (matches == 1) return match;
        // Tie among (0,0) candidates only: if no real-position entry survived the filter and all
        // survivors carry the same weapon id, they are copies of one unit standing on the corner
        // tile -- return the first deterministically rather than refusing.
        if (matches > 1 && !real && tieHomogenous)
            return (exacts > 0 ? firstExact : firstMatch);
        return 0;
    }

    /// <summary>Append EVERY band entry passing the weapon + fingerprint checks to
    /// <paramref name="results"/> (no twin filtering; no tie guard). Intended for callers that
    /// write idempotent values and want every copy covered -- the live entry takes effect and
    /// any frozen twins are inert.</summary>
    public static void LocateAll(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                 (int lvl, int br, int fa) fp, List<long> results)
    {
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            int wid = mem.U16(e + EntryWeapon);
            if (!Contains(hands, wid)) continue;
            if (!Band.LevelMatchesRoster(fp.lvl, mem.U8(e + Offsets.ALevel))) continue;   // fp.lvl is the pre-battle roster level
            if (mem.U8(e + Offsets.ABrave) != fp.br || mem.U8(e + Offsets.AFaith) != fp.fa) continue;
            results.Add(e);
        }
    }

    private static bool Contains(IReadOnlyList<int> hands, int wid)
    {
        for (int i = 0; i < hands.Count; i++) if (hands[i] == wid) return true;
        return false;
    }

    /// <summary>DEV diagnostic: when Locate misses, dump every valid band entry's locate-relevant
    /// fields next to what was wanted -- one log read names the rejecting predicate (weapon field
    /// content, brave/faith mismatch, or the twin filter). Dev-pulse callers only.</summary>
    public static void DumpCandidates(IGameMemory mem, IReadOnlyList<int> hands, (int lvl, int br, int fa) fp)
    {
        Log.Info($"locate-miss: could not find the wielder's combat entry this pulse -- wanted weapon id in [{string.Join(",", hands)}], brave {fp.br}, faith {fp.fa}; candidates listed below");
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            Log.Info($"  cand slot {s}: weapon {mem.U16(e + EntryWeapon)} (wanted one of [{string.Join(",", hands)}]), " +
                     $"level {mem.U8(e + Offsets.ALevel)}, brave {mem.U8(e + Offsets.ABrave)}, faith {mem.U8(e + Offsets.AFaith)}, " +
                     $"position ({mem.U8(e + Offsets.AGx)},{mem.U8(e + Offsets.AGy)})");
        }
    }
}
