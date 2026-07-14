namespace LivingWeapon;

/// <summary>
/// DEV-ONLY convenience grant: ensures the player always has at least one Scholar's Ring (item id
/// 260) available, so the Treasure Master disarm oracle can be tested without shop trips.
/// Called out of battle only; idempotent -- writes 1 iff count[260] currently reads 0.
///
/// Address: Offsets.InventoryCountBase + Offsets.ScholarRingItemId (= 0x1411A7C00 + 260).
/// Source: docs/DEV_TEST_RECIPES.md inventory-give recipe.
///
/// Production compiles Grant to a no-op (LW-86, owner decision 2026-07-14): a shipped build must
/// never write items into a player's save for a disarmed, removal-slated module (Treasure Master,
/// LW-10; the 2026-07-11 fresh-save grant incident). The gate lives here, not at the Engine call
/// site, so the prod contract is directly unit-testable (ScholarRingTests, compiled without LWDEV
/// like the prod/CI build).
/// </summary>
internal static class ScholarRing
{
#if LWDEV
    private static readonly long _addr = Offsets.InventoryCountBase + Offsets.ScholarRingItemId;

    private static bool _granted;   // log once per session
#endif

    /// <summary>
    /// LWDEV: reads count[260] via <paramref name="mem"/>; writes 1 iff it reads 0.
    /// Guards Readable before reading and Writable before writing -- never crashes on a
    /// bad address.  Safe to call every ~1 s out of battle.
    /// Outside LWDEV (production): compiled no-op, never touches <paramref name="mem"/>.
    /// </summary>
    public static void Grant(IGameMemory mem)
    {
#if LWDEV
        if (!mem.Readable(_addr, 1)) return;

        byte count = mem.U8(_addr);
        if (count != 0) return;

        if (!mem.Writable(_addr, 1)) return;

        mem.W8(_addr, 1);

        if (!_granted)
        {
            _granted = true;
            ModLogger.Event(LogVerb.Treasure, "Granted a Scholar's Ring; you had none.");
        }
#endif
    }
}
