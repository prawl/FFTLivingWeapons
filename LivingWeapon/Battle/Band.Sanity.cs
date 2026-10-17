namespace LivingWeapon;

/// <summary>
/// LW-149 stage B: the shared per-band-slot sanity read that the signature modules' band walks (Plague, Ricochet,
/// Larceny, Maim, Kobu, Benediction, Puppeteer) used to duplicate. It guards the maxHp read, then rejects phantom slots:
/// maxHp outside 1..1999 (exclusive bound, see <see cref="Band.EnemyFingerprints"/> for why it differs from the static-array
/// sweep's inclusive 2000), level outside 1..99, or brave/faith outside 1..100. The hp-reading overload serves every caller but Plague.
/// Per-module extras (Ricochet's gx/gy &gt; 30 reject, Puppeteer's job byte, Larceny's buff snapshot) stay caller-side.
/// LW-153 adds <see cref="SameUnitAtExact"/>, the check every held write runs before re-squeezing a stored band address.
/// </summary>
internal static partial class Band
{
    /// <summary>Reads and sanity-checks band slot <paramref name="slot"/>'s core fingerprint fields. False (no further reads) when maxHp is
    /// unreadable or a field fails its bound. <paramref name="addr"/> is always the slot's <see cref="Entry"/>; <paramref name="fp"/> is default on false.</summary>
    internal static bool TryReadUnit(IGameMemory mem, int slot, out long addr,
                                     out (int mhp, int lvl, int br, int fa) fp)
    {
        addr = Entry(slot);
        fp = default;
        if (!mem.Readable(addr + Offsets.AMaxHp, 2)) return false;
        int mhp = mem.U16(addr + Offsets.AMaxHp), lvl = mem.U8(addr + Offsets.ALevel);
        if (mhp < 1 || mhp >= 2000 || lvl < 1 || lvl > 99) return false;
        int br = mem.U8(addr + Offsets.ABrave), fa = mem.U8(addr + Offsets.AFaith);
        if (br < 1 || br > 100 || fa < 1 || fa > 100) return false;
        fp = (mhp, lvl, br, fa);
        return true;
    }

    /// <summary>TryReadUnit plus the guarded current-HP read, for every caller but Plague.</summary>
    internal static bool TryReadUnit(IGameMemory mem, int slot, out long addr,
                                     out (int mhp, int lvl, int br, int fa) fp, out int hp)
    {
        if (!TryReadUnit(mem, slot, out addr, out fp)) { hp = 0; return false; }
        hp = mem.Readable(addr + Offsets.AHp, 2) ? mem.U16(addr + Offsets.AHp) : 0;
        return true;
    }

    /// <summary>Same-unit write-safety check used by Puppeteer's hold, Maim's drive and Plague's drive before re-squeezing a
    /// stored band address. Is the unit at <paramref name="addr"/> still the one whose fingerprint was captured at latch time? Reads maxHp
    /// under guard, then requires all four fields equal. Band slots are fixed addresses and units migrate between them, so a stale
    /// address must read as a stranger and be skipped. Replaces the copies in Puppeteer.Hold.Valid, Maim.Drive and Plague.DriveOne.
    /// Two lookalikes are different rules and stay separate. Rapture.SameUnit is brave/faith-only with fail-safe reads, and
    /// PlaguePolicy.SameVictim is LW-92 drift-tolerant (accepts a bounded mid-battle level-up).</summary>
    internal static bool SameUnitAtExact(IGameMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp)
    {
        if (!mem.Readable(addr + Offsets.AMaxHp, 2)) return false;
        return mem.U16(addr + Offsets.AMaxHp) == fp.mhp && mem.U8(addr + Offsets.ALevel) == fp.lvl
            && mem.U8(addr + Offsets.ABrave) == fp.br && mem.U8(addr + Offsets.AFaith) == fp.fa;
    }
}
