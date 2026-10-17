using System.Collections.Generic;

namespace LivingWeapon;

internal sealed partial class GrowthEngine
{
    /// <summary>Find (and cache) this player's combat struct. Tier 1 needs an exact frame-nameId
    /// match (skipped when rosterNameId &lt;= 0, i.e. roster capture failed); tier 2 is the older
    /// fingerprint scan with the nameId veto (D2/D5). A cached struct is revalidated at the tier it
    /// was found at, or a tier-2 hit would force a full rescan every tick (S4).</summary>
    internal long LocateStruct(int slot, int level, int brave, int faith,
                               List<(int weapon, WeaponMeta m)> hands, int rosterNameId)
    {
        if (_structForSlot.TryGetValue(slot, out var cached)
            && CacheValid(cached, level, brave, faith, hands, rosterNameId))
            return cached.addr;
        return LocateIn(slot, level, brave, faith, hands, LiveEntries(), rosterNameId);
    }

    /// <summary>Revalidate a cached struct at the tier it was found at (S4). A tier-1 cache needs
    /// an exact nameId match; tier 2 (or a tier-1 cache whose roster nameId went stale) needs the
    /// base fingerprint plus the veto.</summary>
    private bool CacheValid((long addr, int tier) cached, int level, int brave, int faith,
                            List<(int weapon, WeaponMeta m)> hands, int rosterNameId)
    {
        if (cached.tier == 1 && rosterNameId > 0)
            return MatchesEntry(_mem, cached.addr, level, brave, faith, hands, rosterNameId);
        if (!MatchesEntry(_mem, cached.addr, level, brave, faith, hands)) return false;
        return PassesVeto(_mem, cached.addr, rosterNameId);
    }

    /// <summary>Scan range around the anchor. Player-side slots (0..CombatSearchSlots) are tagged
    /// player, enemy-side slots (-CombatSearchSlots..-1) enemy.</summary>
    private static List<(bool isPlayer, long addr)> LiveEntries()
    {
        var list = new List<(bool, long)>(2 * Offsets.CombatSearchSlots + 1);
        for (int n = 0; n <= Offsets.CombatSearchSlots; n++)
            list.Add((true, Offsets.CombatAnchor + (long)n * Offsets.CombatStride));
        for (int n = -Offsets.CombatSearchSlots; n <= -1; n++)
            list.Add((false, Offsets.CombatAnchor + (long)n * Offsets.CombatStride));
        return list;
    }

    /// <summary>Locate over an explicit entry list (live callers pass <see cref="LiveEntries"/>,
    /// tests pass pinned buffers). Tier 1 runs when rosterNameId &gt; 0. Zero candidates (S6) fall
    /// through to tier 2, but a multi-candidate refusal returns 0, since a mid-equip weapon
    /// collision clears itself next tick. Tier 2 narrows the pool with
    /// <see cref="VetEntries"/> and reuses the same tie-break (D3). Returns the address, or 0.</summary>
    internal long LocateIn(int slot, int level, int brave, int faith,
                           List<(int weapon, WeaponMeta m)> hands,
                           IReadOnlyList<(bool isPlayer, long addr)> entries, int rosterNameId = 0)
    {
        if (rosterNameId > 0)
        {
            long tier1 = ScanEntries(_mem, level, brave, faith, hands, entries, rosterNameId, out int matchCount1);
            if (tier1 != 0)
            {
                _structForSlot[slot] = (tier1, 1);
                if (!_logged) { _logged = true; ModLogger.Debug(LogVerb.Growth, $"located party slot {slot} in memory; stat growth will apply"); }
                return tier1;
            }
            if (matchCount1 > 0)   // S6: candidates existed, genuinely ambiguous -- refuse, no tier-2 fallthrough
            {
                if (_ambiguousLogged.Add(slot))
                    ModLogger.Warn(LogVerb.Growth, $"Growth is skipped for party slot {slot}; it could not be matched to one unique unit ({matchCount1} candidates, ambiguous fingerprint).");
                return 0;
            }
        }
        var vetted = rosterNameId > 0 ? VetEntries(_mem, entries, rosterNameId) : entries;
        long found = ScanEntries(_mem, level, brave, faith, hands, vetted, 0, out int matchCount2);
        if (found != 0)
        {
            _structForSlot[slot] = (found, 2);
            if (!_logged) { _logged = true; ModLogger.Debug(LogVerb.Growth, $"located party slot {slot} in memory; stat growth will apply"); }
            // D2: only logged when tier 1 ran and came up empty.
            if (rosterNameId > 0 && _fallbackLogged.Add(slot))
                ModLogger.Debug(LogVerb.Growth, $"matched party slot {slot} by its stats alone, not by its engine id; the id was blank this battle (fingerprint fallback, frame nameId miss)");
            return found;
        }
        if (matchCount2 > 1 && _ambiguousLogged.Add(slot))
            ModLogger.Warn(LogVerb.Growth, $"Growth is skipped for party slot {slot}; it could not be matched to one unique unit ({matchCount2} candidates, ambiguous fingerprint).");
        return 0;
    }

    /// <summary>Test hook for whether this slot's ambiguous-locate warning was logged this battle.</summary>
    internal bool IsAmbiguousLogged(int slot) => _ambiguousLogged.Contains(slot);

    /// <summary>Test hook for whether this slot's tier-2 fallback was logged this battle.</summary>
    internal bool IsFallbackLogged(int slot) => _fallbackLogged.Contains(slot);

    /// <summary>Test-only cache seed, so a test can drive the revalidate path against a pinned buffer.</summary>
    internal void SeedStructForSlotForTest(int slot, long addr, int tier) => _structForSlot[slot] = (addr, tier);

    /// <summary>D2 veto pre-filter. Drops entries whose frame nameId is a foreign nonzero value
    /// before the fingerprint rescan. Only called when rosterNameId &gt; 0 (D5).</summary>
    private static List<(bool isPlayer, long addr)> VetEntries(IGameMemory mem,
        IReadOnlyList<(bool isPlayer, long addr)> entries, int rosterNameId)
    {
        var vetted = new List<(bool isPlayer, long addr)>(entries.Count);
        foreach (var e in entries)
            if (PassesVeto(mem, e.addr, rosterNameId)) vetted.Add(e);
        return vetted;
    }

    /// <summary>D2 veto for one address. An entry whose frame nameId is nonzero and differs from
    /// rosterNameId is excluded. Unseeded (0) or unreadable entries pass, and the check is inert
    /// when rosterNameId &lt;= 0.</summary>
    private static bool PassesVeto(IGameMemory mem, long s, int rosterNameId)
    {
        if (rosterNameId <= 0) return true;
        long nameAddr = s + Offsets.BandEntry + Offsets.ANameId;
        if (!mem.Readable(nameAddr, 2)) return true;   // unreadable: veto can't apply, passes
        int entryNameId = mem.U16(nameAddr);
        return entryNameId == 0 || entryNameId == rosterNameId;
    }

    /// <summary>Scan core. Player side is tried first and wins if it matches, homogeneity is tracked
    /// across matches, and homogeneous multi-matches go to the twin tie-break. nameId 0 is
    /// fingerprint mode (never reads the frame nameId, D5); &gt; 0 is tier-1 exact match. Returns
    /// the pick, or 0 on no match, a heterogeneous multi-match, or the LW-252 stage 6 refusal in
    /// <see cref="PickHomogeneousTwin"/>.</summary>
    internal static long ScanEntries(IGameMemory mem, int level, int brave, int faith,
                                     List<(int weapon, WeaponMeta m)> hands,
                                     IReadOnlyList<(bool isPlayer, long addr)> entries, int nameId, out int matchCount)
    {
        long found = 0; matchCount = 0; int tieWeapon = -1; bool homogeneous = true;
        for (int pass = 0; pass < 2 && matchCount == 0; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(mem, addr, level, brave, faith, hands, nameId)) continue;
                int wid = mem.U16(addr + Offsets.CWeapon);
                if (tieWeapon == -1) tieWeapon = wid;
                else if (tieWeapon != wid) homogeneous = false;
                matchCount++; found = addr;
            }
        }
        if (matchCount == 1) return found;
        if (matchCount > 1 && homogeneous)
            return PickHomogeneousTwin(mem, level, brave, faith, hands, entries, nameId);
        return 0;
    }

    /// <summary>Overload for callers that don't need the match count.</summary>
    internal static long ScanEntries(IGameMemory mem, int level, int brave, int faith,
                                     List<(int weapon, WeaponMeta m)> hands,
                                     IReadOnlyList<(bool isPlayer, long addr)> entries, int nameId = 0)
        => ScanEntries(mem, level, brave, faith, hands, entries, nameId, out _);

    /// <summary>Twin tie-break for a homogeneous multi-match. Prefers a real-position (nonzero gx
    /// or gy) entry, else the first at (0,0).
    /// LW-252 stage 6: with nameId &lt;= 0 (no verified identity) and 2+ real-position candidates,
    /// refuse. A wrong-twin hold is a wrong-unit write, and the per-turn normalize can bake a hold
    /// into the save baseline (ledger [per-turn-normalize-repaints-boosted-baseline], UNCERTAIN;
    /// LW-90). One real candidate, a real+(0,0) mirror pair, and any nameId &gt; 0 match resolve as before.</summary>
    private static long PickHomogeneousTwin(IGameMemory mem, int level, int brave, int faith,
                                            List<(int weapon, WeaponMeta m)> hands,
                                            IReadOnlyList<(bool isPlayer, long addr)> entries, int nameId)
    {
        long first = 0, firstReal = 0;
        int realCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(mem, addr, level, brave, faith, hands, nameId)) continue;
                if (first == 0) first = addr;
                bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
                if (realPos)
                {
                    if (firstReal == 0) firstReal = addr;
                    realCount++;
                }
            }
        }
        if (nameId <= 0 && realCount >= 2) return 0;   // decision-4 exception (b): refuse, never guess
        return firstReal != 0 ? firstReal : first;      // all at (0,0): return first deterministically
    }

    /// <summary>True if S is a readable combat struct matching this unit (level, brave/faith, a
    /// weapon field equal to either wielded hand, sane PA/MA). nameId 0 is fingerprint mode and
    /// never reads s+0x1FC (S3, legacy 256-byte pinned test structs). nameId &gt; 0 also requires
    /// the frame nameId (s + BandEntry + ANameId) to match, behind its own Readable check (D8).</summary>
    internal static bool MatchesEntry(IGameMemory mem, long s, int level, int brave, int faith,
                                      List<(int weapon, WeaponMeta m)> hands, int nameId = 0)
    {
        if (!mem.Readable(s, StructSpan)) return false;
        int cw = mem.U16(s + Offsets.CWeapon);
        if (!hands.Exists(x => x.weapon == cw)) return false;
        if (!Band.LevelMatchesRoster(level, mem.U8(s + Offsets.CLevel))) return false;   // level = pre-battle roster value
        if (mem.U8(s + Offsets.CBrave) != brave || mem.U8(s + Offsets.CFaith) != faith) return false;
        int pa = mem.U8(s + Offsets.CPa), ma = mem.U8(s + Offsets.CMa);
        if (pa < StatMin || pa > SigStatHi || ma < StatMin || ma > SigStatHi) return false;
        if (nameId <= 0) return true;   // fingerprint mode: never touches s+0x1FC (S3)
        long nameAddr = s + Offsets.BandEntry + Offsets.ANameId;
        if (!mem.Readable(nameAddr, 2)) return false;   // tier-1 exact mode: unreadable fails (D8)
        return mem.U16(nameAddr) == nameId;
    }
}
