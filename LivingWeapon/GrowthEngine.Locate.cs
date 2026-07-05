using System.Collections.Generic;

namespace LivingWeapon;

internal sealed partial class GrowthEngine
{
    /// <summary>Find (and cache) this player's combat struct. TWO-TIER (D2/D5): tier 1 requires an
    /// exact frame-nameId match (skipped when rosterNameId &lt;= 0 -- roster capture failed, e.g.
    /// an unpopulated battle type); tier 2 is today's pre-nameId fingerprint scan, veto-hardened.
    /// A cached struct is revalidated with the SAME tier it was located at (S4) -- caching a
    /// tier-2 struct under tier-1's stricter bar would force a full rescan every tick, forever.</summary>
    internal long LocateStruct(int slot, int level, int brave, int faith,
                               List<(int weapon, WeaponMeta m)> hands, int rosterNameId)
    {
        if (_structForSlot.TryGetValue(slot, out var cached)
            && CacheValid(cached, level, brave, faith, hands, rosterNameId))
            return cached.addr;
        return LocateIn(slot, level, brave, faith, hands, LiveEntries(), rosterNameId);
    }

    /// <summary>Revalidate a cached struct with the predicate matching the TIER it was found at
    /// (S4): a tier-1 cache needs rosterNameId still positive and still exact-matching; a tier-2
    /// cache (or a tier-1 cache whose roster nameId capture has since gone stale) only needs the
    /// base fingerprint plus an inert-or-passing veto.</summary>
    private bool CacheValid((long addr, int tier) cached, int level, int brave, int faith,
                            List<(int weapon, WeaponMeta m)> hands, int rosterNameId)
    {
        if (cached.tier == 1 && rosterNameId > 0)
            return MatchesEntry(_mem, cached.addr, level, brave, faith, hands, rosterNameId);
        if (!MatchesEntry(_mem, cached.addr, level, brave, faith, hands)) return false;
        return PassesVeto(_mem, cached.addr, rosterNameId);
    }

    /// <summary>The live scan range: player-side slots (n = 0..+CombatSearchSlots) tagged
    /// player, enemy-side (n = -CombatSearchSlots..-1) tagged enemy, around the anchor.</summary>
    private static List<(bool isPlayer, long addr)> LiveEntries()
    {
        var list = new List<(bool, long)>(2 * Offsets.CombatSearchSlots + 1);
        for (int n = 0; n <= Offsets.CombatSearchSlots; n++)
            list.Add((true, Offsets.CombatAnchor + (long)n * Offsets.CombatStride));
        for (int n = -Offsets.CombatSearchSlots; n <= -1; n++)
            list.Add((false, Offsets.CombatAnchor + (long)n * Offsets.CombatStride));
        return list;
    }

    /// <summary>Match + tie-break + cache + throttled log over an explicit entry list -- the ONE
    /// locate path (the live caller passes <see cref="LiveEntries"/>; tests pass pinned buffers,
    /// so they exercise this exact code). TIER 1 (rosterNameId &gt; 0): an exact-nameId ScanEntries
    /// pass; a ZERO-candidate result (matchCount == 0, S6 -- NOT a refused multi-match) falls
    /// through to tier 2, while a multi-candidate refusal returns 0 WITHOUT falling through (a
    /// transient mid-equip heterogeneous-weapon collision self-heals next tick). TIER 2 pre-
    /// filters the entry pool by the nameId veto (<see cref="VetEntries"/>) so ScanEntries' SAME
    /// tie-break/homogeneity logic runs unchanged between tiers (D3) -- only the pool narrows.
    /// Trailing optional rosterNameId (default 0) keeps every pre-existing 6-arg call compiling
    /// unchanged -- the fallback-parity property every pre-nameId test proves. Returns the
    /// located address, or 0 on refusal.</summary>
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
            // Diagnostic for the "nameId unpopulated battle type" premise (D2): only fires when
            // tier 1 actually ran and came up empty, not on every ordinary tier-2-only battle.
            if (rosterNameId > 0 && _fallbackLogged.Add(slot))
                ModLogger.Debug(LogVerb.Growth, $"matched party slot {slot} by its stats alone, not by its engine id; the id was blank this battle (fingerprint fallback, frame nameId miss)");
            return found;
        }
        if (matchCount2 > 1 && _ambiguousLogged.Add(slot))
            ModLogger.Warn(LogVerb.Growth, $"Growth is skipped for party slot {slot}; it could not be matched to one unique unit ({matchCount2} candidates, ambiguous fingerprint).");
        return 0;
    }

    /// <summary>True when the given slot's ambiguous-locate was already logged this battle
    /// (throttle observability for tests; cleared by ResetBattle).</summary>
    internal bool IsAmbiguousLogged(int slot) => _ambiguousLogged.Contains(slot);

    /// <summary>True when the given slot's tier-2 fallback was already logged this battle
    /// (throttle observability for tests; cleared by ResetBattle).</summary>
    internal bool IsFallbackLogged(int slot) => _fallbackLogged.Contains(slot);

    /// <summary>Test-only cache seed (mirrors GunSlinger.StoreForTest): lets a test drive
    /// LocateStruct's revalidate path directly against a pinned buffer without needing the real
    /// LiveEntries() addresses to be mapped in the test process.</summary>
    internal void SeedStructForSlotForTest(int slot, long addr, int tier) => _structForSlot[slot] = (addr, tier);

    /// <summary>D2's veto pre-filter: drop any entry whose frame nameId is a foreign nonzero
    /// value BEFORE the plain fingerprint re-scan runs, so ScanEntries' shared tie-break logic
    /// stays identical between tiers (D3) -- only the candidate pool narrows. Only called when
    /// rosterNameId &gt; 0 (LocateIn skips this entirely otherwise, matching D5's fingerprint-mode-
    /// never-touches-+0x1FC rule).</summary>
    private static List<(bool isPlayer, long addr)> VetEntries(IGameMemory mem,
        IReadOnlyList<(bool isPlayer, long addr)> entries, int rosterNameId)
    {
        var vetted = new List<(bool isPlayer, long addr)>(entries.Count);
        foreach (var e in entries)
            if (PassesVeto(mem, e.addr, rosterNameId)) vetted.Add(e);
        return vetted;
    }

    /// <summary>D2's veto check for one combat-struct/frame address: when rosterNameId is
    /// positive, an entry whose frame nameId reads NONZERO and differs from it is excluded; an
    /// entry reading 0 (unseeded) or unreadable passes. Inert (always true) when rosterNameId
    /// &lt;= 0. Guarded (Readable pre-filter, D8's GrowthEngine.MatchesEntry style -- this is the
    /// same pinned-buffer/LiveMemory combat-struct territory, not the unguarded ReadHp/Wielder
    /// band-address style).</summary>
    private static bool PassesVeto(IGameMemory mem, long s, int rosterNameId)
    {
        if (rosterNameId <= 0) return true;
        long nameAddr = s + Offsets.BandEntry + Offsets.ANameId;
        if (!mem.Readable(nameAddr, 2)) return true;   // unreadable: veto can't apply, passes
        int entryNameId = mem.U16(nameAddr);
        return entryNameId == 0 || entryNameId == rosterNameId;
    }

    /// <summary>The scan core: player-side pass first (exits before enemy-side is considered when
    /// it matches), homogeneity tracked across matches, the twin tie-break applied to homogeneous
    /// multi-matches. <paramref name="nameId"/> threads to MatchesEntry (D5): 0 = fingerprint mode
    /// (today's exact behavior, NEVER reads the frame nameId); &gt; 0 = tier-1 exact-match mode.
    /// Returns the pick, or 0 (with <paramref name="matchCount"/> for the caller's ambiguity log)
    /// when nothing matches or a heterogeneous multi-match refuses.</summary>
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

    /// <summary>Convenience overload for callers that don't need the match count. Trailing
    /// optional nameId (default 0 = fingerprint mode) keeps every pre-existing 6-arg call
    /// compiling unchanged -- the fallback-parity property every pre-nameId test proves.</summary>
    internal static long ScanEntries(IGameMemory mem, int level, int brave, int faith,
                                     List<(int weapon, WeaponMeta m)> hands,
                                     IReadOnlyList<(bool isPlayer, long addr)> entries, int nameId = 0)
        => ScanEntries(mem, level, brave, faith, hands, entries, nameId, out _);

    /// <summary>Re-scan a homogeneous multi-match (all matches share the same weapon id) and
    /// apply the twin tie-break: prefer a real-position (non-zero gx or gy) entry; if all are at
    /// (0,0) return the first deterministically.</summary>
    private static long PickHomogeneousTwin(IGameMemory mem, int level, int brave, int faith,
                                            List<(int weapon, WeaponMeta m)> hands,
                                            IReadOnlyList<(bool isPlayer, long addr)> entries, int nameId)
    {
        long first = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(mem, addr, level, brave, faith, hands, nameId)) continue;
                if (first == 0) first = addr;
                bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
                if (realPos) return addr;
            }
        }
        return first;   // all at (0,0): return first deterministically
    }

    /// <summary>True if S is a readable combat struct matching this unit (level + brave/faith +
    /// its weapon field equals either wielded hand + sane PA/MA). <paramref name="nameId"/> (D5):
    /// 0 = fingerprint mode -- NEVER reads s+0x1FC (S3: legacy 256-byte pinned test structs must
    /// never be touched there); &gt; 0 = ALSO requires the frame nameId (s + BandEntry + ANameId
    /// == s + 0x1FC) to equal it, behind its OWN Readable(addr, 2) pre-filter outside the
    /// StructSpan guard (D8) -- unreadable in this mode fails the match (tier 2's veto may still
    /// take the entry). Internal so tests can drive it directly via pinned buffers.</summary>
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
