using System.Collections.Generic;

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

    /// <summary>Resolve the engine's own ActorPtr global (Offsets.ActorPtr) to the acting unit's
    /// band entry address, or 0 when the pointer is invalid: zero, below FrameReadBase, not
    /// stride-aligned to a whole seat, or naming a seat outside BandSlots. Live-proven
    /// 2026-07-01 (tools/probes/unitid_probe.py "watch"): the pointer named each acting wielder's
    /// own seat during a 2x id-42 repro at the exact instant the turn-queue fingerprint was
    /// ambiguous. Callers apply their own <see cref="IsValid"/> where they need field sanity --
    /// this only validates the POINTER shape, not the pointed-to entry's contents.</summary>
    public static long ActorEntry(IGameMemory mem)
    {
        ulong ptr = mem.U64(Offsets.ActorPtr);
        if (ptr == 0) return 0;
        long frame = (long)ptr;
        if (frame < Offsets.FrameReadBase) return 0;
        long delta = frame - Offsets.FrameReadBase;
        if (delta % Offsets.CombatStride != 0) return 0;
        long seat = delta / Offsets.CombatStride;
        if (seat >= Offsets.BandSlots) return 0;
        return frame + Offsets.BandEntry;
    }

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

    /// <summary>ENEMY fingerprints from the static array (slots 0..EnemySlotMax) -- the shared
    /// enemy-side oracle behind CharmLock/EagleEye/Maim/Ricochet. Shared caveat: the static
    /// array FREEZES on battle restart (capture already happened).
    /// NOTE: the mhp bound here is 2000 INCLUSIVE -- the bound every live-proven scan shipped
    /// with -- and deliberately differs from <see cref="IsValid"/>'s exclusive (&gt;= 2000)
    /// band-entry bound. Documented drift; do not align it without a live probe.</summary>
    public static HashSet<(int mhp, int lvl, int br, int fa)> EnemyFingerprints(IGameMemory mem)
        => Fingerprints(mem, 0, Offsets.EnemySlotMax);

    /// <summary>PLAYER-side fingerprints from the static array (slots above EnemySlotMax) --
    /// the positive ally oracle (Wyrmblood's splash filter). Same freeze caveat and the same
    /// inclusive-2000 mhp bound as <see cref="EnemyFingerprints"/>.</summary>
    public static HashSet<(int mhp, int lvl, int br, int fa)> AllyFingerprints(IGameMemory mem)
        => Fingerprints(mem, Offsets.EnemySlotMax + 1, Offsets.NSlots - 1);

    /// <summary>One static-array sweep: every slot in [first..last] with a sane fingerprint
    /// (filters phantoms -- level 0 / garbage). HashSet = one entry per unit.</summary>
    private static HashSet<(int mhp, int lvl, int br, int fa)> Fingerprints(IGameMemory mem, int first, int last)
    {
        var set = new HashSet<(int, int, int, int)>();
        for (int s = first; s <= last; s++)
        {
            long slot = Offsets.ArrayReadBase + (long)s * Offsets.ArrayStride;
            if (!mem.Readable(slot + Offsets.AMaxHp, 2)) continue;
            int mhp = mem.U16(slot + Offsets.AMaxHp), lvl = mem.U8(slot + Offsets.ALevel);
            int br = mem.U8(slot + Offsets.ABrave), fa = mem.U8(slot + Offsets.AFaith);
            if (mhp < 1 || mhp > 2000 || lvl < 1 || lvl > 99 || br > 100 || fa > 100) continue;
            set.Add((mhp, lvl, br, fa));
        }
        return set;
    }
}
