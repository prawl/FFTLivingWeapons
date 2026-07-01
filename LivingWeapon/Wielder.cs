using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Shared wielder location for weapon-keyed signatures: resolve the SINGLE roster unit
/// holding a weapon (fingerprint + hand item ids), then find that unit's LIVE band entry.
/// Ports ExtraTurn's proven ResolveWielder/Locate pair -- twin filter included -- behind
/// IGameMemory so the walk is unit-testable with the fake (the live caller passes a
/// LiveMemory; reads stay RPM-backed and fail-safe).
/// </summary>
internal static class Wielder
{
    /// <summary>Weapon id inside the band-entry frame: CWeapon(0x20) - BandEntry(0x1C) = +0x04.</summary>
    private const int EntryWeapon = Offsets.CWeapon - Offsets.BandEntry;

    /// <summary>Resolve the single roster slot holding <paramref name="weaponId"/> in either hand
    /// into its (level,brave,faith) fingerprint and its real hand item ids. False when no roster
    /// slot -- or more than one (ambiguous) -- holds the weapon.</summary>
    public static bool TryResolve(IGameMemory mem, int weaponId,
                                  out (int lvl, int br, int fa) fp, List<int> hands)
    {
        fp = default;
        hands.Clear();
        int found = 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int lvl = mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;   // empty slot
            int rh = mem.U16(rb + Offsets.RRHand), lh = mem.U16(rb + Offsets.RLHand), oh = mem.U16(rb + Offsets.ROffHand);
            if (rh != weaponId && lh != weaponId && oh != weaponId) continue;
            if (++found > 1) return false;       // two wielders: ambiguous, no grant
            fp = (lvl, mem.U8(rb + Offsets.RBrave), mem.U8(rb + Offsets.RFaith));
            hands.Clear();
            foreach (int id in new[] { rh, lh, oh })
                if (id != 0x00FF && id != 0xFFFF && !hands.Contains(id)) hands.Add(id);
        }
        return found == 1;
    }

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

    /// <summary>Main-hand-only variant of <see cref="TryResolve"/>: resolves only the roster slot
    /// whose RRHand field equals <paramref name="weaponId"/>. An offhand-only match returns false.
    /// The <paramref name="hands"/> list is populated with just the main-hand id (exact band-field
    /// match for <see cref="Locate"/>). A Living Weapon earns kills in any hand, but commands its
    /// gift only from the main hand.</summary>
    public static bool TryResolveMainHand(IGameMemory mem, int weaponId,
                                          out (int lvl, int br, int fa) fp, List<int> hands)
    {
        fp = default;
        hands.Clear();
        int found = 0;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int lvl = mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;                         // empty slot
            if (mem.U16(rb + Offsets.RRHand) != weaponId) continue;    // main-hand match only
            if (++found > 1) return false;                              // two main-hand wielders: ambiguous
            fp = (lvl, mem.U8(rb + Offsets.RBrave), mem.U8(rb + Offsets.RFaith));
            hands.Clear();
            hands.Add(weaponId);   // locate set = main-hand id only (exact band-field match)
        }
        return found == 1;
    }

    /// <summary>Collect EVERY deployed main-hand wielder of <paramref name="weaponId"/> into
    /// <paramref name="results"/> (cleared first). Each result is the live band entry address
    /// paired with the bearer's (lvl,br,fa) fingerprint. A benched reserve (no band entry) is
    /// silently skipped; two deployed bearers both appear. Intended for Choir, which projects a
    /// separate aura per bearer rather than bailing on ambiguity.</summary>
    public static void ResolveDeployedMainHandAll(IGameMemory mem, int weaponId,
        List<(long entry, (int lvl, int br, int fa) fp)> results)
    {
        results.Clear();
        var hand = new List<int> { weaponId };
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int lvl = mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;                        // empty slot
            if (mem.U16(rb + Offsets.RRHand) != weaponId) continue;   // main-hand match only
            var candFp = (lvl, (int)mem.U8(rb + Offsets.RBrave), (int)mem.U8(rb + Offsets.RFaith));
            long addr = Locate(mem, weaponId, hand, candFp);
            if (addr == 0) continue;                                   // benched / not in this battle -> skip
            results.Add((addr, candFp));
        }
    }

    /// <summary>The single DEPLOYED main-hand wielder of <paramref name="weaponId"/>: scan the roster
    /// for every slot holding it in the main hand, LOCATE each in the live band, and return the one
    /// actually on the battlefield. A benched reserve (no band entry) or an enemy copy the dev give-all
    /// armed is skipped, so a duplicate that isn't in THIS battle no longer creates the false ambiguity
    /// that froze Larceny (<see cref="TryResolveMainHand"/> bailed on the raw roster count). Returns the
    /// wielder's live band entry + its fingerprint, or 0 when zero or MORE THAN ONE deployed wielder is
    /// found (two on-field wielders are still genuinely ambiguous -- rare; refine to the acting one later).</summary>
    public static long ResolveDeployedMainHand(IGameMemory mem, int weaponId, out (int lvl, int br, int fa) fp)
    {
        var list = new List<(long entry, (int lvl, int br, int fa) fp)>();
        ResolveDeployedMainHandAll(mem, weaponId, list);
        if (list.Count == 1) { fp = list[0].fp; return list[0].entry; }
        fp = default;
        return 0;
    }

    /// <summary>True when AT LEAST ONE deployed unit holds <paramref name="weaponId"/> as its main
    /// hand (a roster main-hand wielder that also has a live band entry this battle). Unlike
    /// <see cref="ResolveDeployedMainHand"/> it does NOT bail on two wielders -- the question is only
    /// "is this weapon in play", so a main-hand signature can suppress its gate logging for a weapon
    /// nobody is fielding (a seeded/give-all reserve banks kills -> looks tier-eligible -> spams the
    /// gate every turn even though it is benched).</summary>
    public static bool AnyDeployedMainHand(IGameMemory mem, int weaponId)
    {
        var hand = new List<int> { weaponId };
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int lvl = mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;                        // empty slot
            if (mem.U16(rb + Offsets.RRHand) != weaponId) continue;   // main-hand match only
            var candFp = (lvl, (int)mem.U8(rb + Offsets.RBrave), (int)mem.U8(rb + Offsets.RFaith));
            if (Locate(mem, weaponId, hand, candFp) != 0) return true;   // deployed in this battle
        }
        return false;
    }

    /// <summary>The arm-time identity capture for Iai's mirror-churn-proof release (rebuilt
    /// 2026-07-01): scan the roster for the slot(s) whose main hand holds
    /// <paramref name="weaponId"/> AND whose (level,brave,faith) match <paramref name="fp"/>,
    /// and return that slot's roster nameId (Offsets.RNameId). Returns -1 when NO slot matches,
    /// or when more than one matching slot carries a DISTINCT nameId (ambiguous capture) -- a
    /// single matching slot whose nameId reads 0 (unseeded/invalid) is returned AS 0, not -1; the
    /// caller's guard is "holdNameId &gt; 0", so both -1 and 0 mean "capture failed, fall back to
    /// address matching" without conflating the two failure shapes in this helper.</summary>
    public static int RosterNameId(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp)
    {
        int found = -1;
        bool any = false;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            long rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
            int lvl = mem.U8(rb + Offsets.RLevel);
            if (lvl < 1 || lvl > 99) continue;                        // empty slot
            if (mem.U16(rb + Offsets.RRHand) != weaponId) continue;    // main-hand match only
            if (lvl != fp.lvl || mem.U8(rb + Offsets.RBrave) != fp.br || mem.U8(rb + Offsets.RFaith) != fp.fa) continue;
            int nameId = mem.U16(rb + Offsets.RNameId);
            if (!any) { found = nameId; any = true; }
            else if (found != nameId) return -1;                       // distinct nameIds: ambiguous
        }
        return any ? found : -1;
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
