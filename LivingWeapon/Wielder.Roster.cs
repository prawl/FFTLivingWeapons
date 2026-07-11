using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Wielder's ROSTER-address-space half: resolve the SINGLE roster unit holding a weapon
/// (fingerprint + hand item ids) purely from roster fields, with no band read. Ports
/// ExtraTurn's proven ResolveWielder pair -- twin filter lives in the band half, Wielder.cs --
/// behind IGameMemory so the walk is unit-testable with the fake (the live caller passes a
/// LiveMemory; reads stay RPM-backed and fail-safe).
/// </summary>
internal static partial class Wielder
{
    /// <summary>The ONE shared occupied-slot walk seam (LW-62): every roster resolver and the
    /// existence check ride this so the slot base arithmetic and the occupancy rule (level 1..99)
    /// cannot drift apart per caller.</summary>
    private static bool TryOccupiedSlot(IGameMemory mem, int r, out long rb, out int lvl)
    {
        rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
        lvl = mem.U8(rb + Offsets.RLevel);
        return lvl >= 1 && lvl <= 99;
    }

    /// <summary>Appends the sentinel-filtered, de-duplicated hand ids (rh, lh, oh order) to
    /// <paramref name="hands"/> without allocating a temp array per slot; shared by
    /// <see cref="TryResolve"/> and <see cref="HasLiveWielder"/>.</summary>
    private static void CollectHands(int rh, int lh, int oh, List<int> hands)
    {
        if (rh != 0x00FF && rh != 0xFFFF && !hands.Contains(rh)) hands.Add(rh);
        if (lh != 0x00FF && lh != 0xFFFF && !hands.Contains(lh)) hands.Add(lh);
        if (oh != 0x00FF && oh != 0xFFFF && !hands.Contains(oh)) hands.Add(oh);
    }

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
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
            int rh = mem.U16(rb + Offsets.RRHand), lh = mem.U16(rb + Offsets.RLHand), oh = mem.U16(rb + Offsets.ROffHand);
            if (rh != weaponId && lh != weaponId && oh != weaponId) continue;
            if (++found > 1) return false;       // two wielders: ambiguous, no grant
            fp = (lvl, mem.U8(rb + Offsets.RBrave), mem.U8(rb + Offsets.RFaith));
            hands.Clear();
            CollectHands(rh, lh, oh, hands);
        }
        return found == 1;
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
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
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
    /// separate aura per bearer rather than bailing on ambiguity.
    /// D6: this loop already has each slot's OWN roster nameId in hand (rb + RNameId) -- read it
    /// directly and pass it to the explicit-nameId Locate overload rather than re-resolving it
    /// through the any-hand scan (no re-resolve round trip). Two same-fp deployed bearers stay
    /// disambiguated per-slot (Choir's pinned two-bearer behavior).</summary>
    public static void ResolveDeployedMainHandAll(IGameMemory mem, int weaponId,
        List<(long entry, (int lvl, int br, int fa) fp)> results)
    {
        var full = new List<(long entry, (int lvl, int br, int fa) fp, int nameId)>();
        ResolveDeployedMainHandAllCore(mem, weaponId, full);
        results.Clear();
        foreach (var r in full) results.Add((r.entry, r.fp));
    }

    /// <summary>D7 (2026-07-04): the shared roster walk behind <see cref="ResolveDeployedMainHandAll"/>
    /// and the nameId-carrying <see cref="ResolveDeployedMainHand(IGameMemory,int,out (int,int,int),out int)"/>
    /// overload -- one loop, two result shapes, so the nameId a caller needs (Puppeteer's mirror-safe
    /// pointer identity match) doesn't require a second roster scan.</summary>
    private static void ResolveDeployedMainHandAllCore(IGameMemory mem, int weaponId,
        List<(long entry, (int lvl, int br, int fa) fp, int nameId)> results)
    {
        results.Clear();
        var hand = new List<int> { weaponId };
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
            if (mem.U16(rb + Offsets.RRHand) != weaponId) continue;   // main-hand match only
            var candFp = (lvl, (int)mem.U8(rb + Offsets.RBrave), (int)mem.U8(rb + Offsets.RFaith));
            int rosterNameId = mem.U16(rb + Offsets.RNameId);          // this slot's own back-reference (u16, fail-safe)
            long addr = Locate(mem, weaponId, hand, candFp, rosterNameId);
            if (addr == 0) continue;                                   // benched / not in this battle -> skip
            results.Add((addr, candFp, rosterNameId));
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
        => ResolveDeployedMainHand(mem, weaponId, out fp, out _);

    /// <summary>D7 (2026-07-04): nameId-carrying sibling of the 3-arg overload above -- identical
    /// single/ambiguous-deployed-wielder resolution, but also returns the resolved slot's OWN roster
    /// nameId (0 when that slot's nameId read is unseeded, same fail-safe convention as every other
    /// nameId consumer; also 0 on the zero/ambiguous-wielder return). Puppeteer's pointer-path arming
    /// (2026-07-04 mirror-copy false-negative fix) uses this to compare the acting unit's identity
    /// against the wielder by nameId rather than by raw band address -- see
    /// Puppeteer.Policy.PointerNamesWielder.</summary>
    public static long ResolveDeployedMainHand(IGameMemory mem, int weaponId,
        out (int lvl, int br, int fa) fp, out int rosterNameId)
    {
        var list = new List<(long entry, (int lvl, int br, int fa) fp, int nameId)>();
        ResolveDeployedMainHandAllCore(mem, weaponId, list);
        if (list.Count == 1) { fp = list[0].fp; rosterNameId = list[0].nameId; return list[0].entry; }
        fp = default;
        rosterNameId = 0;
        return 0;
    }

    /// <summary>True when AT LEAST ONE deployed unit holds <paramref name="weaponId"/> as its main
    /// hand (a roster main-hand wielder that also has a live band entry this battle). Unlike
    /// <see cref="ResolveDeployedMainHand"/> it does NOT bail on two wielders -- the question is only
    /// "is this weapon in play", so a main-hand signature can suppress its gate logging for a weapon
    /// nobody is fielding (a seeded/give-all reserve banks kills -> looks tier-eligible -> spams the
    /// gate every turn even though it is benched). D6: reads each slot's own nameId directly, same as
    /// <see cref="ResolveDeployedMainHandAll"/> -- no re-resolve round trip.</summary>
    public static bool AnyDeployedMainHand(IGameMemory mem, int weaponId)
    {
        var hand = new List<int> { weaponId };
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
            if (mem.U16(rb + Offsets.RRHand) != weaponId) continue;   // main-hand match only
            var candFp = (lvl, (int)mem.U8(rb + Offsets.RBrave), (int)mem.U8(rb + Offsets.RFaith));
            int rosterNameId = mem.U16(rb + Offsets.RNameId);
            if (Locate(mem, weaponId, hand, candFp, rosterNameId) != 0) return true;   // deployed in this battle
        }
        return false;
    }

    /// <summary>LW-56: true when AT LEAST ONE roster row holding <paramref name="weaponId"/> in
    /// ANY hand (right, left, or off) has a live band entry backing it right now: the credit
    /// gate's existence check (does this weapon have a deployed wielder on the field at all).
    /// Unlike <see cref="AnyDeployedMainHand"/> (main-hand only, used by the console relevance
    /// gate), this scans all three hand fields: a corpse credit's culprit list can name a
    /// dual-wielder's OFF-hand blade, whose live band entry's own weapon field mirrors the MAIN
    /// hand, not the queried id, so the row's full hand set (sentinel-filtered, mirroring
    /// <see cref="TryResolve"/>) is what gets passed to <see cref="Locate"/> rather than just
    /// weaponId itself.
    /// NO ambiguity bail of any kind (the reviewed blocker): an existence check does not care how
    /// many roster rows hold the weapon, so this exhausts every occupied slot and returns true on
    /// the FIRST slot whose Locate call finds a live band entry, rather than bailing when more
    /// than one roster row holds the weapon (a benched duplicate copy must never poison the
    /// answer for a genuinely deployed copy). The credit gate (<see cref="CreditGate"/>, wired
    /// through KillTracker.CreditKill) is this method's only consumer.</summary>
    public static bool HasLiveWielder(IGameMemory mem, int weaponId)
    {
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
            int rh = mem.U16(rb + Offsets.RRHand), lh = mem.U16(rb + Offsets.RLHand), oh = mem.U16(rb + Offsets.ROffHand);
            if (rh != weaponId && lh != weaponId && oh != weaponId) continue;   // this row doesn't hold it at all
            var fp = (lvl, (int)mem.U8(rb + Offsets.RBrave), (int)mem.U8(rb + Offsets.RFaith));
            var hands = new List<int>();
            CollectHands(rh, lh, oh, hands);
            int rosterNameId = mem.U16(rb + Offsets.RNameId);
            if (Locate(mem, weaponId, hands, fp, rosterNameId) != 0) return true;   // deployed: done
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
        ScanNameIdMatches(mem, weaponId, fp, mainHandOnly: true, out int count, out int first, out bool allSame);
        if (count == 0) return -1;
        return allSame ? first : -1;                                   // distinct nameIds among matches: ambiguous
    }

    /// <summary>D4's any-hand sibling of <see cref="RosterNameId"/>, used by <see cref="Locate"/>'s
    /// public (implicit-resolve) overload: match roster slots by weaponId in EITHER hand (rh/lh/oh)
    /// AND fp equality. Returns the nameId iff EXACTLY ONE roster slot matched; returns -1 on ZERO
    /// or MORE THAN ONE matching slot -- UNLIKE <see cref="RosterNameId"/>, this refuses even when
    /// every matching slot carries the SAME nameId (duplicated roster nameIds are a documented live
    /// corner, Iai.cs:74-77; a locate write must never pick between two units). RosterNameId's own
    /// main-hand/distinct-nameId contract is UNCHANGED -- Iai's arm-time capture depends on it.</summary>
    internal static int ResolveAnyHandNameId(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp)
    {
        ScanNameIdMatches(mem, weaponId, fp, mainHandOnly: false, out int count, out int first, out _);
        return count == 1 ? first : -1;                                 // >1 slot: refuse regardless of nameId equality
    }

    /// <summary>Shared roster walk behind <see cref="RosterNameId"/> and <see cref="ResolveAnyHandNameId"/>
    /// (D4 -- no duplicated roster scan): count every roster slot holding <paramref name="weaponId"/>
    /// (main hand only, or any of rh/lh/oh per <paramref name="mainHandOnly"/>) whose (level,brave,faith)
    /// equals <paramref name="fp"/>, and report the first match's nameId plus whether every match shares
    /// that same nameId. The two callers interpret <paramref name="count"/>/<paramref name="allSameNameId"/>
    /// under DIFFERENT multi-match policies -- see each method's own doc comment.</summary>
    private static void ScanNameIdMatches(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp,
        bool mainHandOnly, out int count, out int firstNameId, out bool allSameNameId)
    {
        count = 0; firstNameId = -1; allSameNameId = true;
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
            int rh = mem.U16(rb + Offsets.RRHand);
            if (mainHandOnly)
            {
                if (rh != weaponId) continue;                          // main-hand match only
            }
            else
            {
                int lh = mem.U16(rb + Offsets.RLHand), oh = mem.U16(rb + Offsets.ROffHand);
                if (rh != weaponId && lh != weaponId && oh != weaponId) continue;   // any hand
            }
            if (lvl != fp.lvl || mem.U8(rb + Offsets.RBrave) != fp.br || mem.U8(rb + Offsets.RFaith) != fp.fa) continue;
            int nameId = mem.U16(rb + Offsets.RNameId);
            if (count == 0) firstNameId = nameId;
            else if (nameId != firstNameId) allSameNameId = false;
            count++;
        }
    }
}
