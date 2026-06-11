namespace LivingWeapon;

/// <summary>
/// Shared helpers for reading auth-band entries. The band stays live through battle
/// restarts (the static array freezes; the band does not). Entry layout mirrors the
/// static-array A* offsets (ALevel 0x0D, ABrave 0x0E, etc.). Live-proven: fresh
/// corpse 0/539 visible only in the band; Ramza's real grid position only there.
/// </summary>
internal static class Band
{
    /// <summary>How far a LIVE level may exceed the ROSTER level and still identify the same
    /// unit. The roster keeps the pre-battle level until battle end while live structs update
    /// on a mid-battle level-up -- exact equality made freshly-leveled units invisible to
    /// every roster-keyed identification (live 2026-06-10: a Phoenix Down kill by a leveled-up
    /// Ramza paid out to the Wellspring Rod through the stale actor latch, and a leveled
    /// wielder silently dropped their growth and signatures). Levels only go UP mid-battle,
    /// so the window is one-sided; 9 bounds even an absurd grinding session within a battle
    /// without reopening the enemy-collision hole the level checks exist to close.</summary>
    public const int MaxLevelDrift = 9;

    /// <summary>True when a LIVE level is consistent with a unit whose ROSTER level was
    /// recorded pre-battle: equal, or above by at most <see cref="MaxLevelDrift"/>.</summary>
    public static bool LevelMatchesRoster(int rosterLevel, int liveLevel)
        => liveLevel >= rosterLevel && liveLevel - rosterLevel <= MaxLevelDrift;

    /// <summary>Base address for band slot s (s = 0..BandSlots-1, mapping n=-24..+24).</summary>
    public static long Entry(int s) => Offsets.BandReadBase + (long)s * Offsets.CombatStride;

    /// <summary>True iff the band entry at <paramref name="addr"/> has sane field values.
    /// brave/faith reach 100 via Praise/Steel -- the upper bound is 100, not 99.
    /// AInBattle (+0x12) reads 0 for live enemies: do NOT gate on it in the band.</summary>
    public static bool IsValid(IGameMemory mem, long addr)
    {
        int lvl = mem.U8(addr + Offsets.ALevel);
        if (lvl < 1 || lvl > 99) return false;
        int br = mem.U8(addr + Offsets.ABrave);
        if (br < 1 || br > 100) return false;
        int fa = mem.U8(addr + Offsets.AFaith);
        if (fa < 1 || fa > 100) return false;
        int mhp = mem.U16(addr + Offsets.AMaxHp);
        if (mhp < 1 || mhp >= 2000) return false;
        int gx = mem.U8(addr + Offsets.AGx);
        int gy = mem.U8(addr + Offsets.AGy);
        return gx <= 30 && gy <= 30;
    }
}
