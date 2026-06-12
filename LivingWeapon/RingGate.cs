namespace LivingWeapon;

/// <summary>
/// Detects whether the Scholar's Ring (item id 260) is equipped in any party member's
/// accessory slot, using the roster accessory field at roster offset +0x12 (RAccessory).
///
/// Called once per battle from TreasureMaster.TickDisarmed when the map id has stabilised
/// and alwaysOn is false.  The result is cached for the battle (re-read on ResetBattle).
///
/// Pure read via IGameMemory -- never static Mem, never a write.
/// </summary>
internal static class RingGate
{
    /// <summary>
    /// Returns true if any roster slot (0..RosterSlots-1) has the Scholar's Ring
    /// (Offsets.ScholarRingItemId = 260) in its accessory field (RosterBase + slot*RosterStride
    /// + RAccessory, read as u16).  Unreadable slots are skipped without error.
    /// </summary>
    internal static bool ScholarRingEquipped(IGameMemory mem)
    {
        for (int slot = 0; slot < Offsets.RosterSlots; slot++)
        {
            long rb   = Offsets.RosterBase + (long)slot * Offsets.RosterStride;
            long addr = rb + Offsets.RAccessory;
            if (!mem.Readable(addr, 2)) continue;
            if (mem.U16(addr) == Offsets.ScholarRingItemId) return true;
        }
        return false;
    }
}
