using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Band-address-space half of Wielder. Given a roster-resolved fingerprint and hand-id set
/// (Wielder.Roster.cs), find that unit's live band entry. Ports ExtraTurn's Locate pair, twin
/// filter included, behind IGameMemory so the walk is testable with the fake.
/// The LW-252 ambiguous-roster branch is in Wielder.Ambiguous.cs.
/// </summary>
internal static partial class Wielder
{
    /// <summary>Weapon id inside the band-entry frame. CWeapon(0x20), BandEntry(0x1C) = +0x04.</summary>
    private const int EntryWeapon = Offsets.CWeapon - Offsets.BandEntry;

    /// <summary>The wielder's live band entry. Resolves the roster nameId from the hand ids and
    /// delegates to the explicit overload below. If 2+ roster rows match it routes to
    /// <see cref="LocateAmbiguous"/> instead. Before LW-252 that case resolved to the same -1 as
    /// zero matches, which disarmed tier 2's foreign-nameId veto and let a band collider be adopted
    /// (ledger [party-nameid-unique-key], mirror mechanism [frame-1fc-nameid-mirror]). Zero matches,
    /// or a 2+ roster with an unseeded row, still take the old tier-2-only path with rosterNameId -1.</summary>
    public static long Locate(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                              (int lvl, int br, int fa) fp)
    {
        int rosterNameId = ResolveAnyHandNameId(mem, weaponId, fp, out int matchCount);
        return matchCount >= 2
            ? LocateAmbiguous(mem, weaponId, hands, fp)
            : Locate(mem, weaponId, hands, fp, rosterNameId);
    }

    /// <summary>Two-tier locate. Tier 1 runs when <paramref name="rosterNameId"/> is positive and
    /// returns the frame-nameId-verified hit; if candidates existed but the tie stayed ambiguous it
    /// returns 0 without falling to tier 2 (S6). Tier 2 is the plain fingerprint scan with a veto:
    /// an entry whose frame nameId is nonzero and differs from rosterNameId is excluded, so an enemy
    /// fp-collider can't be returned when the wielder's entry is absent. Composite resolvers in
    /// Wielder.Roster.cs call this directly to skip the any-hand re-resolve.</summary>
    internal static long Locate(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                (int lvl, int br, int fa) fp, int rosterNameId)
    {
        if (rosterNameId > 0)
        {
            long hit = LocateTier1(mem, weaponId, hands, fp, rosterNameId, out int candidatesSeen);
            if (hit != 0) return hit;
            if (candidatesSeen > 0) return 0;   // saw candidates, refused. Do not fall through (S6)
        }
        return LocateTier2(mem, weaponId, hands, fp, rosterNameId);
    }

    /// <summary>Tier-1 predicate and tie-break. A candidate must pass <see cref="BasePredicate"/>
    /// plus frame nameId == <paramref name="rosterNameId"/>, read unguarded as in Iai.cs ~137-140
    /// (Mem's U16 returns 0 on an unreadable address, and 0 never matches since rosterNameId &gt; 0).
    /// Real position beats (0,0) and exact weapon beats hand match. Unlike tier 2, the tie-break also
    /// fires for several real-position survivors, gated by an inline nameId-homogeneity check so a
    /// looser per-entry gate can't widen it to a mixed-identity tie.
    /// Accepted residuals (D3, do not fix without a live counter-probe): which nameId-verified mirror
    /// copy is returned is unknown, but holds re-apply every tick so a miss self-heals; and an enemy
    /// matching nameId, full fp and weapon id is indistinguishable from a mirror copy.
    /// Internal for <see cref="LocateAmbiguous"/> (LW-252, as TryOccupiedSlot in LW-149), which must
    /// call it directly because the 5-arg <see cref="Locate(IGameMemory,int,IReadOnlyList{int},(int,int,int),int)"/>
    /// falls through to tier 2 and would reopen the collider leak.</summary>
    internal static long LocateTier1(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                    (int lvl, int br, int fa) fp, int rosterNameId, out int candidatesSeen)
    {
        candidatesSeen = 0;
        long match = 0, exact = 0;
        int matches = 0, exacts = 0;
        bool real = false;
        long firstMatch = 0, firstExact = 0;
        int tieNameId = -1; bool tieHomogenous = true;
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            if (!BasePredicate(mem, e, hands, fp, out int wid)) continue;
            int entryNameId = mem.U16(e + Offsets.ANameId);   // unguarded fail-safe read (Iai pattern, D8)
            if (entryNameId != rosterNameId) continue;         // D1 gate (rosterNameId > 0 here. No 0==0 trap)
            candidatesSeen++;
            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            if (real && !realPos) continue;                                  // (0,0) twin loses to a live match
            if (realPos && !real)
            {
                real = true; matches = 0; exacts = 0; exact = 0;
                firstMatch = 0; firstExact = 0; tieNameId = -1; tieHomogenous = true;
            }
            matches++; match = e; if (firstMatch == 0) firstMatch = e;
            if (wid == weaponId) { exacts++; exact = e; if (firstExact == 0) firstExact = e; }
            if (tieNameId == -1) tieNameId = entryNameId;
            else if (tieNameId != entryNameId) tieHomogenous = false;
        }
        if (exacts == 1) return exact;
        if (matches == 1) return match;
        // Relaxed tie (D3): unlike tier 2, also fires for real-position survivors; the nameId match
        // shows they are copies of one unit.
        if (matches > 1 && tieHomogenous) return exacts > 0 ? firstExact : firstMatch;
        return 0;
    }

    /// <summary>The fingerprint scan (the original pre-nameId algorithm) plus a veto. When
    /// <paramref name="rosterNameId"/> is positive, an entry with a nonzero frame nameId that differs
    /// is excluded; an entry reading 0 still passes. With rosterNameId &lt;= 0 it behaves as before.</summary>
    private static long LocateTier2(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                    (int lvl, int br, int fa) fp, int rosterNameId)
    {
        long match = 0, exact = 0;
        int matches = 0, exacts = 0;
        bool real = false;   // current candidates carry a real grid position
        // Track all surviving candidates for the identical-twin tie-break.
        long firstMatch = 0, firstExact = 0;
        int tieWid = -1; bool tieHomogenous = true;   // all candidates share the same weapon id?
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            if (!BasePredicate(mem, e, hands, fp, out int wid)) continue;
            if (rosterNameId > 0)
            {
                int entryNameId = mem.U16(e + Offsets.ANameId);
                if (entryNameId != 0 && entryNameId != rosterNameId) continue;   // D2 veto. Foreign nonzero nameId excluded
            }
            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            if (real && !realPos) continue;                                  // (0,0) twin loses to a live match
            if (realPos && !real)
            {
                real = true; matches = 0; exacts = 0; exact = 0;   // match reset omitted. Reassigned unconditionally below
                firstMatch = 0; firstExact = 0; tieWid = -1; tieHomogenous = true;
            }
            matches++; match = e; if (firstMatch == 0) firstMatch = e;
            if (wid == weaponId) { exacts++; exact = e; if (firstExact == 0) firstExact = e; }
            // Track homogeneity. All candidates must share the same weapon-id at their entry.
            if (tieWid == -1) tieWid = wid;
            else if (tieWid != wid) tieHomogenous = false;
        }
        if (exacts == 1) return exact;
        if (matches == 1) return match;
        // Tie among (0,0) candidates only. With no real-position survivor and one shared weapon id,
        // they are copies of one unit on the corner tile, so return the first.
        if (matches > 1 && !real && tieHomogenous)
            return (exacts > 0 ? firstExact : firstMatch);
        return 0;
    }

    /// <summary>Per-entry gate for both tiers. Weapon id is one of the hands, level is
    /// roster-consistent (<see cref="Band.LevelMatchesRoster"/>), brave/faith match <paramref name="fp"/>.
    /// Outputs the entry's weapon id for the exact-vs-hand tie-break.</summary>
    private static bool BasePredicate(IGameMemory mem, long e, IReadOnlyList<int> hands,
                                      (int lvl, int br, int fa) fp, out int wid)
    {
        wid = mem.U16(e + EntryWeapon);
        if (!Contains(hands, wid)) return false;
        if (!Band.LevelMatchesRoster(fp.lvl, mem.U8(e + Offsets.ALevel))) return false;   // fp.lvl is the pre-battle roster level
        if (mem.U8(e + Offsets.ABrave) != fp.br || mem.U8(e + Offsets.AFaith) != fp.fa) return false;
        return true;
    }

    /// <summary>Append every band entry passing the weapon and fingerprint checks to
    /// <paramref name="results"/>, with no twin filtering, for idempotent writes where covering every
    /// copy is harmless. Resolves the roster nameId like <see cref="Locate(IGameMemory,int,IReadOnlyList{int},(int,int,int))"/>,
    /// including its LW-252 branch (<see cref="LocateAllAmbiguous"/>), so 2+ matching roster rows don't
    /// hand a write like SpiritualFont's to two different deployed wielders sharing a weapon.</summary>
    public static void LocateAll(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                 (int lvl, int br, int fa) fp, List<long> results)
    {
        int rosterNameId = ResolveAnyHandNameId(mem, weaponId, fp, out int matchCount);
        if (matchCount >= 2) { LocateAllAmbiguous(mem, weaponId, hands, fp, results); return; }
        LocateAll(mem, weaponId, hands, fp, rosterNameId, results);
    }

    /// <summary>Two-tier LocateAll. If tier 1 (nameId-matching copies) collects anything, that is the
    /// result. Otherwise falls back to the tier-2 fp scan with the same veto as <see cref="LocateTier2"/>,
    /// so a foreign-nameId collider yields an empty result rather than crediting the wrong unit.</summary>
    internal static void LocateAll(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                   (int lvl, int br, int fa) fp, int rosterNameId, List<long> results)
    {
        if (rosterNameId > 0)
        {
            int before = results.Count;
            for (int s = 0; s < Offsets.BandSlots; s++)
            {
                long e = Band.Entry(s);
                if (!Band.IsValid(mem, e)) continue;
                if (!BasePredicate(mem, e, hands, fp, out _)) continue;
                int entryNameId = mem.U16(e + Offsets.ANameId);
                if (entryNameId != rosterNameId) continue;
                results.Add(e);
            }
            if (results.Count > before) return;   // tier 1 found copies. Done, no tier-2 fallback
        }
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            if (!BasePredicate(mem, e, hands, fp, out _)) continue;
            if (rosterNameId > 0)
            {
                int entryNameId = mem.U16(e + Offsets.ANameId);
                if (entryNameId != 0 && entryNameId != rosterNameId) continue;   // D2 veto
            }
            results.Add(e);
        }
    }

    private static bool Contains(IReadOnlyList<int> hands, int wid)
    {
        for (int i = 0; i < hands.Count; i++) if (hands[i] == wid) return true;
        return false;
    }

    /// <summary>DEV diagnostic. On a Locate miss, dump every valid band entry's locate fields next to
    /// what was wanted, so one log read names the rejecting predicate. Dev-pulse callers only.</summary>
    public static void DumpCandidates(IGameMemory mem, IReadOnlyList<int> hands, (int lvl, int br, int fa) fp)
    {
        ModLogger.Debug(LogVerb.Trace, $"wielder search missed this pulse: wanted weapon id in [{string.Join(",", hands)}], brave {fp.br}, faith {fp.fa}; candidates follow");
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            ModLogger.Debug(LogVerb.Trace, $"  candidate slot {s}: weapon {mem.U16(e + EntryWeapon)} (wanted one of [{string.Join(",", hands)}]), " +
                     $"level {mem.U8(e + Offsets.ALevel)}, brave {mem.U8(e + Offsets.ABrave)}, faith {mem.U8(e + Offsets.AFaith)}, " +
                     $"position ({mem.U8(e + Offsets.AGx)},{mem.U8(e + Offsets.AGy)})");
        }
    }
}
