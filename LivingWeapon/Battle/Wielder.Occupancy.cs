namespace LivingWeapon;

/// <summary>
/// Lenient occupied-slot rule for Wielder. The strict walk in Wielder.Roster.cs
/// (<see cref="Wielder.TryOccupiedSlot"/>) needs level 1..99; a few signature gates have always used this
/// weaker check, so it gets its own helper and their behavior stays unchanged.
/// </summary>
internal static partial class Wielder
{
    /// <summary>True when the slot's nameId is readable, with no level gate. Used by EagleEye.ActiveTarget
    /// and Plague.IsEquipped. A level check here would move them onto the strict rule; the ghost-row
    /// tests in EagleEyeTests/PlagueTests catch that.</summary>
    internal static bool TryOccupiedSlotLenient(IGameMemory mem, int r, out long rb)
    {
        rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
        return mem.Readable(rb + Offsets.RNameId, 2);
    }
}
