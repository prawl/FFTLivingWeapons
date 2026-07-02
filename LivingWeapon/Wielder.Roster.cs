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
}
