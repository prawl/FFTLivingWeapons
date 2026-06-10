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
    /// survivors an exact weapon match outranks a hand match; a remaining tie returns 0 (a miss
    /// beats acting on a stranger).</summary>
    public static long Locate(IGameMemory mem, int weaponId, IReadOnlyList<int> hands,
                              (int lvl, int br, int fa) fp)
    {
        long match = 0, exact = 0;
        int matches = 0, exacts = 0;
        bool real = false;   // current candidates carry a real grid position
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long e = Band.Entry(s);
            if (!Band.IsValid(mem, e)) continue;
            int wid = mem.U16(e + EntryWeapon);
            if (!Contains(hands, wid)) continue;
            if (mem.U8(e + Offsets.ABrave) != fp.br || mem.U8(e + Offsets.AFaith) != fp.fa) continue;
            bool realPos = mem.U8(e + Offsets.AGx) != 0 || mem.U8(e + Offsets.AGy) != 0;
            if (real && !realPos) continue;                                  // (0,0) twin loses to a live match
            if (realPos && !real) { real = true; matches = 0; exacts = 0; match = 0; exact = 0; }
            matches++; match = e;
            if (wid == weaponId) { exacts++; exact = e; }
        }
        if (exacts == 1) return exact;
        if (matches == 1) return match;
        return 0;
    }

    private static bool Contains(IReadOnlyList<int> hands, int wid)
    {
        for (int i = 0; i < hands.Count; i++) if (hands[i] == wid) return true;
        return false;
    }
}
