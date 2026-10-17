using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Roster-only wielder resolution: finds the roster unit holding a weapon (fingerprint + hand ids) with no band read.
/// Ports ExtraTurn's ResolveWielder pair; the twin filter lives in Wielder.cs. Goes through IGameMemory so tests can fake it.
/// </summary>
internal static partial class Wielder
{
    /// <summary>Occupied-slot walk (LW-62; internal since LW-149 stage D), shared by the roster resolvers, Choir.cs and RingGate.cs so the
    /// level 1..99 rule lives in one place. EagleEye.ActiveTarget and Plague.IsEquipped use <see cref="TryOccupiedSlotLenient"/>
    /// (Wielder.Occupancy.cs, no level check) so a readable level-0 ghost row still arms them.</summary>
    internal static bool TryOccupiedSlot(IGameMemory mem, int r, out long rb, out int lvl)
    {
        rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
        lvl = mem.U8(rb + Offsets.RLevel);
        return lvl >= 1 && lvl <= 99;
    }

    /// <summary>Appends sentinel-filtered, de-duplicated hand ids (rh, lh, oh order) to <paramref name="hands"/>, no temp array per slot;
    /// shared by <see cref="TryResolve"/> and <see cref="HasLiveWielder"/>.</summary>
    private static void CollectHands(int rh, int lh, int oh, List<int> hands)
    {
        if (rh != 0x00FF && rh != 0xFFFF && !hands.Contains(rh)) hands.Add(rh);
        if (lh != 0x00FF && lh != 0xFFFF && !hands.Contains(lh)) hands.Add(lh);
        if (oh != 0x00FF && oh != 0xFFFF && !hands.Contains(oh)) hands.Add(oh);
    }

    /// <summary>Resolves the one roster slot holding <paramref name="weaponId"/> in either hand into its
    /// (level,brave,faith) fp and hand item ids. False when no slot or more than one holds it.</summary>
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

    /// <summary>Main-hand-only <see cref="TryResolve"/>: an offhand-only match returns false and
    /// <paramref name="hands"/> holds just the main-hand id for <see cref="Locate"/>. A weapon earns kills in
    /// any hand but grants its gift only from the main hand.</summary>
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

    /// <summary>Collects every deployed main-hand wielder of <paramref name="weaponId"/> into <paramref name="results"/> (cleared first):
    /// band entry address, fp, and the roster row's own nameId (LW-252 stage 5), so per-wielder holds (Iai, Mushin) can key through
    /// WielderKeyedStore instead of the fp-only dictionary that shared state between fp-twins. Benched reserves are skipped; used by Choir, Iai, Mushin.</summary>
    public static void ResolveDeployedMainHandAll(IGameMemory mem, int weaponId,
        List<(long entry, (int lvl, int br, int fa) fp, int nameId)> results)
        => ResolveDeployedMainHandAllCore(mem, weaponId, results);

    /// <summary>Shared roster walk behind <see cref="ResolveDeployedMainHandAll"/> and the nameId-carrying
    /// <see cref="ResolveDeployedMainHand(IGameMemory,int,out (int,int,int),out int)"/> overload, so Puppeteer's nameId match
    /// needs no second scan (D7, 2026-07-04).</summary>
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

    /// <summary>The single deployed main-hand wielder of <paramref name="weaponId"/>: locates each roster
    /// main-hand holder in the live band. Benched reserves and give-all enemy copies are skipped, so they no
    /// longer cause the false ambiguity that froze Larceny (<see cref="TryResolveMainHand"/> bailed on the raw
    /// roster count). Returns the band entry and fp, or 0 when none or 2+ are deployed.</summary>
    public static long ResolveDeployedMainHand(IGameMemory mem, int weaponId, out (int lvl, int br, int fa) fp)
        => ResolveDeployedMainHand(mem, weaponId, out fp, out _);

    /// <summary>Same as the 3-arg overload plus the slot's roster nameId (0 when unseeded or no single wielder). Puppeteer matches
    /// the acting unit by nameId because mirror copies made band-address matching miss (2026-07-04, see Puppeteer.Policy.PointerNamesWielder).</summary>
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

    /// <summary>True when at least one deployed unit holds <paramref name="weaponId"/> in its main hand. Does not bail on two
    /// wielders. Lets a main-hand signature skip gate logging for a benched seeded/give-all weapon.</summary>
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

    /// <summary>True when at least one roster row holding <paramref name="weaponId"/> in any hand has a live band entry.
    /// Used by the credit gate (<see cref="CreditGate"/>, via KillTracker.CreditKill). Scans all three hands because a corpse credit can name
    /// a dual-wielder's off-hand blade, whose band entry mirrors the main hand. No ambiguity bail: a benched duplicate must not hide a deployed copy.</summary>
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

    /// <summary>Arm-time identity capture for Iai's mirror-churn-proof release (2026-07-01). Returns the
    /// roster nameId (Offsets.RNameId) of the slot whose main hand holds <paramref name="weaponId"/> and whose
    /// fp matches. Returns -1 when no slot matches or matching slots carry distinct nameIds; a single match
    /// with nameId 0 returns 0. The caller treats both as "fall back to address matching" (guard is
    /// holdNameId &gt; 0).</summary>
    public static int RosterNameId(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp)
    {
        ScanNameIdMatches(mem, weaponId, fp, mainHandOnly: true, out int count, out int first, out bool allSame);
        if (count == 0) return -1;
        return allSame ? first : -1;                                   // distinct nameIds among matches: ambiguous
    }

    /// <summary>Any-hand sibling of <see cref="RosterNameId"/> for <see cref="Locate"/>'s implicit-resolve overload. Returns the nameId
    /// when exactly one slot matches weaponId (rh/lh/oh) and fp, else -1; refuses even when all matches share a nameId, since roster nameIds
    /// can be duplicated (Iai.cs:74-77). -1 alone does not stop Locate (its foreign-nameId veto only arms when rosterNameId &gt; 0), which is how
    /// a band collider got adopted when 2+ rows shared weapon+fp (ledger [party-nameid-unique-key], LW-252); callers that need to tell 0 from 2+
    /// use the 4-arg overload (see <see cref="LocateAmbiguous"/>).</summary>
    internal static int ResolveAnyHandNameId(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp)
        => ResolveAnyHandNameId(mem, weaponId, fp, out _);

    /// <summary>Same rule as the 3-arg overload, plus the raw <paramref name="matchCount"/> so callers can tell zero rows from 2+
    /// (both return -1). Locate/LocateAll send 2+ to their ambiguous branch; WeaponPalette.Desire.cs (DesiredGlow, LW-295)
    /// treats any matchCount &gt;= 1 as a hit.</summary>
    internal static int ResolveAnyHandNameId(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp,
        out int matchCount)
    {
        ScanNameIdMatches(mem, weaponId, fp, mainHandOnly: false, out matchCount, out int first, out _);
        return matchCount == 1 ? first : -1;                            // >1 slot: refuse regardless of nameId equality
    }

    /// <summary>Shared walk behind <see cref="RosterNameId"/> and <see cref="ResolveAnyHandNameId"/> (D4). Counts slots holding
    /// <paramref name="weaponId"/> (main hand or any hand per <paramref name="mainHandOnly"/>) with fp equal to <paramref name="fp"/>,
    /// and reports the first match's nameId and whether all matches share it. Each caller applies its own multi-match policy.</summary>
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

    /// <summary>Every row holding <paramref name="weaponId"/> in any hand with a matching fp, with its own nameId
    /// (0 = unseeded) and hand set (<see cref="CollectHands"/>), since a same-fp twin can carry a different off-hand item.
    /// Separate from <see cref="ScanNameIdMatches"/>, which keeps no per-row list. Used by the ambiguous paths in Wielder.cs
    /// (LocateAmbiguous/LocateAllAmbiguous). <paramref name="rows"/> is cleared first.</summary>
    internal static void CollectAmbiguousRows(IGameMemory mem, int weaponId, (int lvl, int br, int fa) fp,
        List<(long rb, int nameId, List<int> hands)> rows)
    {
        rows.Clear();
        for (int r = 0; r < Offsets.RosterSlots; r++)
        {
            if (!TryOccupiedSlot(mem, r, out long rb, out int lvl)) continue;   // empty slot
            int rh = mem.U16(rb + Offsets.RRHand), lh = mem.U16(rb + Offsets.RLHand), oh = mem.U16(rb + Offsets.ROffHand);
            if (rh != weaponId && lh != weaponId && oh != weaponId) continue;   // this row doesn't hold it at all
            if (lvl != fp.lvl || mem.U8(rb + Offsets.RBrave) != fp.br || mem.U8(rb + Offsets.RFaith) != fp.fa) continue;
            var hands = new List<int>();
            CollectHands(rh, lh, oh, hands);
            rows.Add((rb, mem.U16(rb + Offsets.RNameId), hands));
        }
    }
}
