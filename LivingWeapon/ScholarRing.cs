namespace LivingWeapon;

/// <summary>
/// Ensures the player always has at least one Scholar's Ring (item id 260) available.
/// Called out of battle only; idempotent -- writes 1 iff count[260] currently reads 0.
///
/// Address: Offsets.InventoryCountBase + Offsets.ScholarRingItemId (= 0x1411A17C0 + 260).
/// Source: docs/DEV_TEST_RECIPES.md inventory-give recipe.
/// </summary>
internal static class ScholarRing
{
    private static readonly long _addr = Offsets.InventoryCountBase + Offsets.ScholarRingItemId;

    private static bool _granted;   // log once per session

    /// <summary>
    /// Reads count[260] via <paramref name="mem"/>; writes 1 iff it reads 0.
    /// Guards Readable before reading and Writable before writing -- never crashes on a
    /// bad address.  Safe to call every ~1 s out of battle.
    /// </summary>
    public static void Grant(IGameMemory mem)
    {
        if (!mem.Readable(_addr, 1)) return;

        byte count = mem.U8(_addr);
        if (count != 0) return;

        if (!mem.Writable(_addr, 1)) return;

        mem.W8(_addr, 1);

        if (!_granted)
        {
            _granted = true;
            ModLogger.Log("scholar-ring: granted (you had none)");
        }
    }
}
