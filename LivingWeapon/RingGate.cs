namespace LivingWeapon;

/// <summary>
/// Detects whether the Scholar's Ring (item id 260) is equipped on a party member who is
/// DEPLOYED in the current battle. Battle-only by design: a benched ring-bearer does not count,
/// the same way no other equipped effect applies to a unit that isn't on the field.
///
/// The live battle band stores no accessory id, so the check is two-step:
///   1. find ring-bearers in the roster (accessory u16 == 260 at RosterBase + slot*stride + RAccessory),
///   2. confirm each ring-bearer is present in the live battle band by its (brave, faith)
///      fingerprint + a level-drift-tolerant level match -- the same roster&lt;-&gt;band identity
///      ActorResolver/Band use. A ring-bearer with no matching band entry is benched -&gt; ignored.
///
/// Called from TreasureMaster.TickDisarmed while the module is in a live battle (so the band is
/// populated); the result is cached for the battle (a mid-battle unequip does not drop the marks).
///
/// Pure read via IGameMemory -- never static Mem, never a write.
/// </summary>
internal static class RingGate
{
    /// <summary>
    /// Returns true iff some roster slot holds the Scholar's Ring (Offsets.ScholarRingItemId)
    /// AND that unit is deployed in the live battle band. Unreadable / empty (level 0) roster
    /// slots and benched ring-bearers are skipped.
    /// </summary>
    internal static bool ScholarRingEquipped(IGameMemory mem)
    {
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long rb  = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
            long acc = rb + Offsets.RAccessory;
            if (!mem.Readable(acc, 2)) continue;
            if (mem.U16(acc) != Offsets.ScholarRingItemId) continue;

            // Ring found in the roster -- only counts if its wearer is on the field.
            if (!mem.Readable(rb + Offsets.RLevel, 1)) continue;
            int rLvl = mem.U8(rb + Offsets.RLevel);
            if (rLvl < 1 || rLvl > 99) continue;          // empty / invalid roster slot
            int rBr = mem.U8(rb + Offsets.RBrave);
            int rFa = mem.U8(rb + Offsets.RFaith);
            // D7: this slot's own roster nameId, guarded like GrowthEngine.Apply's read (Readable
            // probe before the U16). UNLIKE Apply, an unreadable capture here does NOT skip the
            // slot -- it defaults to 0 (capture failed), which keeps BandHasUnit's tier 2 running
            // with an inert veto (D2's fallback-parity property: every pre-nameId test, none of
            // which seed RNameId, exercises this exact path unchanged).
            int rosterNameId = mem.Readable(rb + Offsets.RNameId, 2) ? mem.U16(rb + Offsets.RNameId) : 0;
            if (BandHasUnit(mem, rLvl, rBr, rFa, rosterNameId)) return true;
        }
        return false;
    }

    /// <summary>True if ANY roster slot holds the Scholar's Ring, regardless of whether that unit
    /// is deployed. Used only to decide whether to nag "no ring" -- a ring on a benched unit (or
    /// one whose band entry is still loading) must NOT be reported as "no Scholar's Ring".</summary>
    internal static bool ScholarRingInRoster(IGameMemory mem)
    {
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long acc = Offsets.RosterBase + (long)slot * Offsets.RosterStride + Offsets.RAccessory;
            if (!mem.Readable(acc, 2)) continue;
            if (mem.U16(acc) == Offsets.ScholarRingItemId) return true;
        }
        return false;
    }

    /// <summary>True when a live band entry matches (brave, faith) and a level consistent with
    /// the pre-battle roster level (level only drifts UP mid-battle; Band.LevelMatchesRoster).
    /// TWO-TIER-WITH-VETO (D2/D7), mirroring GrowthEngine.ReadHp: tier 1 (<paramref
    /// name="rosterNameId"/> &gt; 0) requires an exact frame-nameId match; tier 2 is today's plain
    /// fingerprint scan, veto-hardened -- an entry whose frame nameId reads NONZERO and differs
    /// from rosterNameId is excluded (closes the wrong-unit hazard where an fp-colliding unit,
    /// enemy or ally, falsely counts as this ring-bearer's deployed match). When rosterNameId
    /// &lt;= 0 (capture failed/unavailable) the veto is inert and this is byte-for-byte today's
    /// behavior -- the fallback-parity property every pre-nameId test proves.</summary>
    private static bool BandHasUnit(IGameMemory mem, int rosterLevel, int brave, int faith, int rosterNameId = 0)
    {
        if (rosterNameId > 0 && BandHasUnitScan(mem, rosterLevel, brave, faith, rosterNameId, exact: true))
            return true;
        return BandHasUnitScan(mem, rosterLevel, brave, faith, rosterNameId, exact: false);
    }

    /// <summary>One full band pass under either mode: <paramref name="exact"/> = tier 1 (requires
    /// the band entry's frame nameId == rosterNameId, unguarded read; only called with rosterNameId
    /// &gt; 0 per D8's Iai/Wielder/ReadHp pattern -- Mem's U16 fail-safes to 0 on an unreadable
    /// address, and 0 never matches here since exact mode only runs when rosterNameId &gt; 0);
    /// !exact = tier 2 (today's fingerprint match, plus the D2 veto when rosterNameId &gt; 0 -- a
    /// foreign nonzero nameId excludes the entry, 0/unseeded passes).</summary>
    private static bool BandHasUnitScan(IGameMemory mem, int rosterLevel, int brave, int faith,
                                        int rosterNameId, bool exact)
    {
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(mem, addr)) continue;
            if (mem.U8(addr + Offsets.ABrave) != brave) continue;
            if (mem.U8(addr + Offsets.AFaith) != faith) continue;
            if (!Band.LevelMatchesRoster(rosterLevel, mem.U8(addr + Offsets.ALevel))) continue;
            if (exact)
            {
                int entryNameId = mem.U16(addr + Offsets.ANameId);   // unguarded fail-safe read (Iai pattern, D8)
                if (entryNameId != rosterNameId) continue;
            }
            else if (rosterNameId > 0)
            {
                int entryNameId = mem.U16(addr + Offsets.ANameId);
                if (entryNameId != 0 && entryNameId != rosterNameId) continue;   // D2 veto
            }
            return true;
        }
        return false;
    }
}
