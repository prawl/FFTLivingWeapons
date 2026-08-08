namespace LivingWeapon;

/// <summary>
/// LW-149 stage B: the shared per-band-slot sanity read every signature module's per-tick
/// band walk duplicated verbatim (Plague, Ricochet, Larceny, Maim, Kobu, Benediction,
/// Puppeteer) -- compute the slot's entry address, guard the maxHp read, then reject an
/// insane/phantom slot: maxHp outside 1..1999 (the band-entry EXCLUSIVE bound at 2000 --
/// see <see cref="Band.EnemyFingerprints"/>'s doc for why it deliberately differs from the static-array
/// fingerprint sweep's INCLUSIVE 2000; this extraction does not touch that separate bound),
/// level outside 1..99, or brave/faith outside 1..100.
///
/// Two overloads: the four-field core (mhp,lvl,br,fa) every one of the seven callers reads,
/// and an hp-reading sibling for the six callers (all but Plague) that also read the guarded
/// current-HP field identically (fail-safe 0 when AHp is unreadable). Plague reads no HP in
/// its band walk, so it uses the narrower overload rather than widen its own read surface.
///
/// Anything a caller does beyond this shared core (Ricochet's gx/gy &gt; 30 reject, Puppeteer's
/// job byte, Larceny's holdable-buff snapshot, ...) stays caller-side: this helper is exactly
/// the six-line block every copy shared, nothing more.
///
/// LW-153 adds the read side's write-safety twin below: <see cref="SameUnitAtExact"/>, the
/// verify every held write runs before re-squeezing a stored band address.
/// </summary>
internal static partial class Band
{
    /// <summary>Read and sanity-check band slot <paramref name="slot"/>'s core fingerprint
    /// fields. Returns false (no further reads attempted) when maxHp is unreadable or any
    /// field fails its bound. <paramref name="addr"/> is always <see cref="Entry"/> for this
    /// slot regardless of outcome; <paramref name="fp"/> is default on a false return.</summary>
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

    /// <summary>TryReadUnit plus the guarded current-HP read the six HP-reading callers all
    /// perform identically. Used by every caller but Plague.</summary>
    internal static bool TryReadUnit(IGameMemory mem, int slot, out long addr,
                                     out (int mhp, int lvl, int br, int fa) fp, out int hp)
    {
        if (!TryReadUnit(mem, slot, out addr, out fp)) { hp = 0; return false; }
        hp = mem.Readable(addr + Offsets.AHp, 2) ? mem.U16(addr + Offsets.AHp) : 0;
        return true;
    }

    /// <summary>The same-unit WRITE-SAFETY predicate its three consumers (Puppeteer's
    /// hold, Maim's drive, Plague's drive) verify before re-squeezing a stored band address
    /// (LW-153): is the unit at <paramref name="addr"/>
    /// still EXACTLY the one whose fingerprint was captured at latch time? Guards the maxHp
    /// read, then demands all four fields equal. Band slots are fixed addresses and units
    /// migrate between them, so a stale address must read as a stranger and be skipped, never
    /// written. Was token-identical in Puppeteer.Hold.Valid and inlined in
    /// Maim.Drive and Plague.DriveOne.
    ///
    /// EXACT is in the name on purpose: two lookalikes are DIFFERENT rules and must NOT fold
    /// in. Rapture.SameUnit is brave/faith-only with fail-safe reads (level-exempt by design),
    /// and PlaguePolicy.SameVictim is LW-92 drift-tolerant (accepts a bounded mid-battle
    /// level-up). The LW-92 lesson is why one home matters: if exact-match ever proves too
    /// strict for another module, the call gets decided HERE once, not re-discovered per
    /// copy.</summary>
    internal static bool SameUnitAtExact(IGameMemory mem, long addr, (int mhp, int lvl, int br, int fa) fp)
    {
        if (!mem.Readable(addr + Offsets.AMaxHp, 2)) return false;
        return mem.U16(addr + Offsets.AMaxHp) == fp.mhp && mem.U8(addr + Offsets.ALevel) == fp.lvl
            && mem.U8(addr + Offsets.ABrave) == fp.br && mem.U8(addr + Offsets.AFaith) == fp.fa;
    }
}
