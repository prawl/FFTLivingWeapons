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

    /// <summary>The active (TurnQueue) unit's full identity: its (level,brave,faith) fingerprint
    /// and the winning REAL-position band-entry address, from a single band walk matching the
    /// condensed active-unit struct's maxHp+hp+level (Offsets.TurnQueue). Never resolves by
    /// nameId (TqNameId is a sequential battle index, a collision trap, see Offsets.TurnQueue's
    /// doc comment). Twin-filtered: a frozen (0,0) duplicate loses to any real-position match.
    /// Returns false when the queue itself is garbage (maxHp 0 or &gt;= 2000, level outside
    /// 1..99), no band entry matches, or the match is genuinely AMBIGUOUS (surviving candidates
    /// carry DISTINCT fingerprints, miss beats mis-credit). Twin entries sharing the SAME
    /// fingerprint are fine. Ported verbatim from TurnTracker.TryActiveFingerprint (which now
    /// delegates here) so every own-turn-detecting signature (FeignDeath, Puppeteer, Mushin,
    /// TurnTracker itself) shares one walk.</summary>
    public static bool ActiveOwner(IGameMemory mem, out (int lvl, int br, int fa) fp, out long entry)
    {
        fp = default;
        entry = 0;
        ushort maxHp = mem.U16(Offsets.TurnQueue + Offsets.TqMaxHp);
        ushort hp = mem.U16(Offsets.TurnQueue + Offsets.TqHp);
        ushort level = mem.U16(Offsets.TurnQueue + Offsets.TqLevel);
        if (maxHp == 0 || maxHp >= 2000 || level < 1 || level > 99) return false;

        (int, int, int) found = default;
        long foundEntry = 0;
        bool haveFp = false;
        bool foundReal = false;   // twin filter: prefer real-position entries

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Entry(s);
            if (!IsValid(mem, addr)) continue;
            if (mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (mem.U16(addr + Offsets.AHp) != hp) continue;
            if (mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
            // twin filter: skip (0,0) entries if we already have a real-position match
            if (foundReal && !realPos) continue;
            if (realPos && !foundReal && haveFp) { found = default; foundEntry = 0; haveFp = false; foundReal = true; }
            if (realPos) foundReal = true;

            var candidate = (level, (int)mem.U8(addr + Offsets.ABrave), (int)mem.U8(addr + Offsets.AFaith));
            if (!haveFp) { found = candidate; foundEntry = addr; haveFp = true; }
            else if (found != candidate) return false;   // distinct fingerprints -> ambiguous
        }
        if (!haveFp) return false;
        fp = found;
        entry = foundEntry;
        return true;
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

    /// <summary>LW-63: the per-unit PSX turn-flags owner (band +0x19C ATurnFlag, PROVEN LIVE
    /// 2026-07-09) -- names the acting unit STRUCTURALLY, unlike the engine actor pointer (which
    /// parks on struck victims and mirror-seat units; the parked-pointer mis-credit this fix
    /// closes, docs/TODO.md LW-63). Walks every band slot for entries with ATurnFlag == 1 (the
    /// exclusive-ownership byte: across 58 flags records over 4 real tapes, never two t=1 units
    /// in one record; AMoved/AActed are NOT exclusive this way -- many units read a1 alongside
    /// the true owner between their own turns, so only t may be tested this way, and only via
    /// the ==1 test: the moved byte reads raw values up to 3, so no flag byte may ever be
    /// treated as a plain boolean).
    ///
    /// REAL POSITION IS MANDATORY (D2b, the blocker fix): a frozen (0,0) band mirror can carry a
    /// stale t=1 (Mushin.cs residual 3) with no external anchor to catch it, so a (0,0) entry is
    /// simply never a candidate here -- it can neither win alone nor create ambiguity against a
    /// same-identity real-position twin.
    ///
    /// Among the remaining (real-position, t=1) candidates: zero -> false (a zero-t record is a
    /// real, tape-verified case -- the battle-opening edge -- not a bug). One -> that entry wins.
    /// Two or more with the SAME identity (ANameId when both are nonzero, else the (level,brave,
    /// faith) fingerprint) -> the first wins (a duplicate, not a disagreement). Two or more with
    /// DIFFERENT identities -> false: an ambiguous flags read must never be guessed between
    /// (miss beats mis-credit) -- the caller falls through to the register/turn-queue chain.</summary>
    public static bool FlagOwner(IGameMemory mem, out long entry, out int slot)
    {
        entry = 0;
        slot = -1;
        bool found = false;
        ushort foundNameId = 0;
        (int lvl, int br, int fa) foundFp = default;

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Entry(s);
            if (!IsValid(mem, addr)) continue;
            if (mem.U8(addr + Offsets.ATurnFlag) != 1) continue;

            bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
            if (!realPos) continue;   // D2b: never a candidate, so it can't win alone or create ambiguity

            ushort nameId = mem.U16(addr + Offsets.ANameId);
            var fp = ((int)mem.U8(addr + Offsets.ALevel), (int)mem.U8(addr + Offsets.ABrave), (int)mem.U8(addr + Offsets.AFaith));

            if (!found)
            {
                entry = addr; slot = s; found = true;
                foundNameId = nameId; foundFp = fp;
                continue;
            }

            bool sameIdentity = (foundNameId == 0 && nameId == 0) ? foundFp == fp : foundNameId == nameId;
            if (!sameIdentity) { entry = 0; slot = -1; return false; }   // two disagreeing t=1 owners -> ambiguous
            // same identity duplicate (a twin) -> the first candidate already won; keep scanning
            // in case a later, genuinely different owner turns up.
        }
        return found;
    }
}
