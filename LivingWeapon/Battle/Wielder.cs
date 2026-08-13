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

    /// <summary>The wielder's LIVE band entry -- PUBLIC entry point, signature UNCHANGED (12
    /// callers untouched). Resolves the roster nameId implicitly (Wielder.ResolveAnyHandNameId,
    /// D4) and delegates to the explicit-nameId overload below. Every existing caller that has
    /// NOT seeded a matching roster nameId resolves -1 here (0 or 2+ matching roster slots), so
    /// this degrades to the identical tier-2-only behavior that shipped before this two-tier
    /// split -- see the tier-2 doc comment's parity note.</summary>
    public static long Locate(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                              (int lvl, int br, int fa) fp)
        => Locate(mem, weaponId, hands, fp, ResolveAnyHandNameId(mem, weaponId, fp));

    /// <summary>Two-tier locate (D2): TIER 1 (below) runs when <paramref name="rosterNameId"/> is
    /// positive and returns a nonzero hit whenever the frame-nameId-verified predicate resolves
    /// one; a tier-1 REFUSAL (candidates existed but the tie stayed genuinely ambiguous) returns 0
    /// WITHOUT falling through to tier 2 (S6 -- matchCount-keyed, not found==0-keyed). TIER 2 is
    /// today's fingerprint scan, veto-hardened: an entry whose frame nameId reads nonzero and
    /// differs from <paramref name="rosterNameId"/> is excluded (closes the wrong-unit hazard --
    /// a wielder entry absent, an enemy fp-collider present, used to hand the enemy's address to
    /// the caller). Callers that already know a specific roster slot's nameId (the composite
    /// resolvers in Wielder.Roster.cs, D6) call this directly to skip the any-hand re-resolve.</summary>
    internal static long Locate(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                (int lvl, int br, int fa) fp, int rosterNameId)
    {
        if (rosterNameId > 0)
        {
            long hit = LocateTier1(mem, weaponId, hands, fp, rosterNameId, out int candidatesSeen);
            if (hit != 0) return hit;
            if (candidatesSeen > 0) return 0;   // saw candidates, refused: do NOT fall through (S6)
        }
        return LocateTier2(mem, weaponId, hands, fp, rosterNameId);
    }

    /// <summary>D1's tier-1 predicate + D3's tier-1 tie-break. A candidate must clear the SAME
    /// weapon/level/brave/faith gate as tier 2 (<see cref="BasePredicate"/>) PLUS frame nameId ==
    /// <paramref name="rosterNameId"/> (read UNGUARDED per D8 -- the shipped Iai pattern, Iai.cs
    /// ~137-140: Mem's U16 fail-safes to 0 on an unreadable address, and 0 never matches here
    /// because this method only runs when rosterNameId &gt; 0 -- the 0==0 trap). Keeps real-
    /// position-beats-(0,0) and exact-weapon-beats-hand-match. UNLIKE tier 2, the deterministic
    /// tie-break also fires when multiple REAL-position candidates survive (not just frozen (0,0)
    /// twins) -- gated by an EXPLICIT nameId-homogeneity check (S1: every tier-1 candidate already
    /// satisfies entryNameId == rosterNameId above, but this re-verifies it inline rather than
    /// trusting that filter "by construction", so a future loosening of the per-entry gate can't
    /// silently widen the relaxation to a genuinely mixed-identity tie).
    /// ACCEPTED RESIDUALS (D3, do not "fix" without a live counter-probe): (1) when the revolving
    /// engine mirror clones a wielder, which of the nameId-verified copies this returns is a live
    /// unknown -- holds re-apply every tick and the mirror revolves, so a mis-targeted tick self-
    /// heals; (2) an enemy colliding on nameId AND full fp AND weapon id simultaneously is
    /// indistinguishable from a mirror copy here -- accepted, strictly narrower than the fp+weapon
    /// collision bail class this whole rebuild replaces.</summary>
    private static long LocateTier1(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
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
            if (entryNameId != rosterNameId) continue;         // D1 gate (rosterNameId > 0 here: no 0==0 trap)
            candidatesSeen++;
            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            if (real && !realPos) continue;                                  // (0,0) twin loses to a live match
            if (realPos && !real) { real = true; matches = 0; exacts = 0; exact = 0;
                                    firstMatch = 0; firstExact = 0; tieNameId = -1; tieHomogenous = true; }
            matches++; match = e; if (firstMatch == 0) firstMatch = e;
            if (wid == weaponId) { exacts++; exact = e; if (firstExact == 0) firstExact = e; }
            if (tieNameId == -1) tieNameId = entryNameId;
            else if (tieNameId != entryNameId) tieHomogenous = false;
        }
        if (exacts == 1) return exact;
        if (matches == 1) return match;
        // Relaxed tie (D3): unlike tier 2, this fires for real-position survivors too -- nameId
        // verification already proved these are copies of ONE identified unit.
        if (matches > 1 && tieHomogenous) return exacts > 0 ? firstExact : firstMatch;
        return 0;
    }

    /// <summary>Today's fingerprint scan (byte-for-byte the pre-nameId Locate algorithm) plus D2's
    /// veto: when <paramref name="rosterNameId"/> is positive, an entry whose frame nameId reads
    /// NONZERO and differs from it is excluded outright -- an entry reading 0 (unseeded, or the
    /// read failed) still passes. When rosterNameId &lt;= 0 the veto guard never triggers, so this
    /// is exactly today's behavior (the fallback-parity property every pre-existing test proves:
    /// every one of them resolves rosterNameId &lt;= 0, so the whole suite exercises this path
    /// unchanged).</summary>
    private static long LocateTier2(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                    (int lvl, int br, int fa) fp, int rosterNameId)
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
            if (!BasePredicate(mem, e, hands, fp, out int wid)) continue;
            if (rosterNameId > 0)
            {
                int entryNameId = mem.U16(e + Offsets.ANameId);
                if (entryNameId != 0 && entryNameId != rosterNameId) continue;   // D2 veto: foreign nonzero nameId excluded
            }
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

    /// <summary>Shared per-entry gate behind both tiers: the entry's weapon id is one of the
    /// wielder's hands, its level is roster-consistent (<see cref="Band.LevelMatchesRoster"/>),
    /// and brave/faith exactly match <paramref name="fp"/>. Outputs the entry's own weapon id
    /// (needed by both tiers' exact-vs-hand tie-break) when it passes.</summary>
    private static bool BasePredicate(IGameMemory mem, long e, IReadOnlyList<int> hands,
                                      (int lvl, int br, int fa) fp, out int wid)
    {
        wid = mem.U16(e + EntryWeapon);
        if (!Contains(hands, wid)) return false;
        if (!Band.LevelMatchesRoster(fp.lvl, mem.U8(e + Offsets.ALevel))) return false;   // fp.lvl is the pre-battle roster level
        if (mem.U8(e + Offsets.ABrave) != fp.br || mem.U8(e + Offsets.AFaith) != fp.fa) return false;
        return true;
    }

    /// <summary>Append EVERY band entry passing the weapon + fingerprint checks to
    /// <paramref name="results"/> (no twin filtering; no tie guard). Intended for callers that
    /// write idempotent values and want every copy covered -- the live entry takes effect and
    /// any frozen twins are inert. PUBLIC entry point, signature UNCHANGED: resolves the roster
    /// nameId implicitly, same as <see cref="Locate(IGameMemory,int,IReadOnlyList{int},(int,int,int))"/>.</summary>
    public static void LocateAll(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                                 (int lvl, int br, int fa) fp, List<long> results)
        => LocateAll(mem, weaponId, hands, fp, ResolveAnyHandNameId(mem, weaponId, fp), results);

    /// <summary>Two-tier LocateAll (D2 analog): when tier 1 (nameId-matching copies only) collects
    /// at least one entry, those are the whole result -- no tier-2 fallback for a battle where
    /// tier 1 genuinely found copies. Otherwise falls back to tier 2 (today's fp scan, veto-
    /// hardened exactly like <see cref="LocateTier2"/>'s veto): a foreign-nameId collider is
    /// excluded even when it is the only fp-matching entry left (results come back empty rather
    /// than crediting the wrong unit).</summary>
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
            if (results.Count > before) return;   // tier 1 found copies: done, no tier-2 fallback
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

    /// <summary>DEV diagnostic: when Locate misses, dump every valid band entry's locate-relevant
    /// fields next to what was wanted -- one log read names the rejecting predicate (weapon field
    /// content, brave/faith mismatch, or the twin filter). Dev-pulse callers only.</summary>
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
