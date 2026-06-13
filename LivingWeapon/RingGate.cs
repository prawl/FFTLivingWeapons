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
            if (BandHasUnit(mem, rLvl, rBr, rFa)) return true;
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
    /// the pre-battle roster level (level only drifts UP mid-battle; Band.LevelMatchesRoster).</summary>
    private static bool BandHasUnit(IGameMemory mem, int rosterLevel, int brave, int faith)
    {
        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Band.Entry(s);
            if (!Band.IsValid(mem, addr)) continue;
            if (mem.U8(addr + Offsets.ABrave) != brave) continue;
            if (mem.U8(addr + Offsets.AFaith) != faith) continue;
            if (Band.LevelMatchesRoster(rosterLevel, mem.U8(addr + Offsets.ALevel))) return true;
        }
        return false;
    }
}
