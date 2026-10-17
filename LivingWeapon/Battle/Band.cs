using System.Collections.Generic;

namespace LivingWeapon;

/// <summary>
/// Helpers for reading auth-band entries. The band stays live through battle restarts while
/// the static array freezes. Entry layout mirrors the static-array A* offsets (ALevel 0x0D,
/// ABrave 0x0E, etc.). A fresh corpse (0/539) is visible only in the band, as is Ramza's grid position.
/// </summary>
internal static partial class Band
{
    /// <summary>Largest amount a live level may exceed the roster level and still match the same
    /// unit. The roster keeps the pre-battle level until battle end, so exact equality hid freshly
    /// leveled units (2026-06-10. A Phoenix Down kill by a leveled Ramza paid the Wellspring Rod).
    /// Levels only rise mid-battle, so the window is one-sided; 9 covers a long grind.</summary>
    public const int MaxLevelDrift = 9;

    /// <summary>True when a live level fits a unit whose roster level was recorded pre-battle.</summary>
    public static bool LevelMatchesRoster(int rosterLevel, int liveLevel)
        => liveLevel >= rosterLevel && liveLevel - rosterLevel <= MaxLevelDrift;

    /// <summary>Base address for band slot s (s = 0..BandSlots-1, mapping n=-24..+24).</summary>
    public static long Entry(int s) => Offsets.BandReadBase + (long)s * Offsets.CombatStride;

    /// <summary>Resolves the engine's ActorPtr global (Offsets.ActorPtr) to the acting unit's band
    /// entry address, or 0 when the pointer is zero, below FrameReadBase, not seat-aligned, or outside
    /// BandSlots. Checked 2026-07-01 with tools/probes/unitid_probe.py "watch" on a 2x id-42 repro.
    /// Only the pointer shape is validated, not the entry contents.</summary>
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

    /// <summary>True when the band entry at <paramref name="addr"/> has sane field values.
    /// Brave/faith reach 100 via Praise/Steel. AInBattle (+0x12) reads 0 for live enemies, so it is not tested.</summary>
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

    /// <summary>The active (TurnQueue) unit's (level,brave,faith) fingerprint and its band entry
    /// address, from one band walk matching the queue's maxHp, hp and level (Offsets.TurnQueue).
    /// Does not use nameId (TqNameId is a sequential battle index, see Offsets.TurnQueue). A frozen
    /// (0,0) duplicate loses to a real-position match. Returns false on a garbage queue, no match, or
    /// candidates with distinct fingerprints (a miss beats a mis-credit). Twins with the same
    /// fingerprint are fine. Moved from TurnTracker.TryActiveFingerprint, which delegates here.</summary>
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
        bool foundReal = false;   // twin filter. Prefer real-position entries

        for (int s = 0; s < Offsets.BandSlots; s++)
        {
            long addr = Entry(s);
            if (!IsValid(mem, addr)) continue;
            if (mem.U16(addr + Offsets.AMaxHp) != maxHp) continue;
            if (mem.U16(addr + Offsets.AHp) != hp) continue;
            if (mem.U8(addr + Offsets.ALevel) != level) continue;

            bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
            // twin filter. Skip (0,0) entries once a real-position match exists
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

    /// <summary>Enemy fingerprints from the static array (slots 0..EnemySlotMax), the oracle behind
    /// EagleEye/Maim/Ricochet. The array freezes on battle restart. The mhp bound is inclusive 2000,
    /// unlike the exclusive bound in <see cref="IsValid"/>; align them only after a probe.</summary>
    public static HashSet<(int mhp, int lvl, int br, int fa)> EnemyFingerprints(IGameMemory mem)
        => Fingerprints(mem, 0, Offsets.EnemySlotMax);

    /// <summary>Player-side fingerprints from the static array (slots above EnemySlotMax), used by
    /// Renewal and Benediction. Same freeze caveat and mhp bound as <see cref="EnemyFingerprints"/>.</summary>
    public static HashSet<(int mhp, int lvl, int br, int fa)> AllyFingerprints(IGameMemory mem)
        => Fingerprints(mem, Offsets.EnemySlotMax + 1, Offsets.NSlots - 1);

    /// <summary>One static-array sweep over [first..last], skipping phantom slots (level 0, garbage).</summary>
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

    /// <summary>the unit that owns the PSX turn flags (band +0x19C ATurnFlag, 2026-07-09).
    /// Unlike the engine actor pointer, which parks on struck victims and mirror-seat units, this
    /// names the acting unit from its own entry (docs/TODO.md LW-63). Across 58 flag records in 4
    /// tapes no two units held t=1 at once. AMoved/AActed are not exclusive, and the moved byte reads
    /// up to 3, so only the ==1 test on ATurnFlag is used.
    /// Entries at (0,0) are skipped (D2b) since a frozen band mirror can carry a stale t=1 (Mushin.cs
    /// residual 3). Zero candidates returns false (the battle-opening edge). Several candidates with the
    /// same identity (ANameId, or the fingerprint when both are 0) return the first. Differing
    /// identities return false and the caller falls back to the register/turn-queue chain.</summary>
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
            if (!realPos) continue;   // D2b

            ushort nameId = mem.U16(addr + Offsets.ANameId);
            var fp = ((int)mem.U8(addr + Offsets.ALevel), (int)mem.U8(addr + Offsets.ABrave), (int)mem.U8(addr + Offsets.AFaith));

            if (!found)
            {
                entry = addr; slot = s; found = true;
                foundNameId = nameId; foundFp = fp;
                continue;
            }

            bool sameIdentity = (foundNameId == 0 && nameId == 0) ? foundFp == fp : foundNameId == nameId;
            if (!sameIdentity) { entry = 0; slot = -1; return false; }   // two disagreeing owners -> ambiguous
            // same identity (a twin): the first already won; keep scanning for a different owner
        }
        return found;
    }
}
