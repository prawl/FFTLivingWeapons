namespace LivingWeapon;

/// <summary>
/// Wielder's SECOND occupied-slot rule (LW-149 stage D). Alongside the STRICT walk in
/// Wielder.Roster.cs (<see cref="Wielder.TryOccupiedSlot"/>, level 1..99), a handful of
/// signature activation gates historically used a weaker LENIENT rule: a roster slot counts as
/// "occupied" whenever its nameId field is READABLE at all (Readable(RNameId, 2)), with no level
/// floor or ceiling. Splitting this into its own named helper -- rather than silently folding
/// these callers onto the strict rule, or leaving the loop copy-pasted three times -- keeps their
/// existing behavior byte-identical: a roster slot with a readable-but-invalid (e.g. 0) level
/// today still arms CharmLock/EagleEye/Plague's activation gate, and must keep doing so. Kept in
/// its own file (rather than folded into the already-long Wielder.Roster.cs) so this real seam
/// stays a real seam, not line-count evasion.
/// </summary>
internal static partial class Wielder
{
    /// <summary>The LENIENT occupied-slot walk: true iff <c>Readable(rb + RNameId, 2)</c>, with NO
    /// level gate. Ridden by CharmLock.ActiveLockTurns, EagleEye.ActiveTarget, and
    /// Plague.IsEquipped (LW-149 stage D migration) -- the exact rule each used before the
    /// migration, only named and shared now instead of copy-pasted three times. Do NOT add a level
    /// check here: that would silently re-tighten these three callers onto the strict rule (see
    /// the ghost-row pin tests in CharmLockTests/EagleEyeTests/PlagueTests, which exist precisely
    /// to catch that regression).</summary>
    internal static bool TryOccupiedSlotLenient(IGameMemory mem, int r, out long rb)
    {
        rb = Offsets.RosterBase + (long)r * Offsets.RosterStride;
        return mem.Readable(rb + Offsets.RNameId, 2);
    }
}
