using System.Collections.Generic;

namespace LivingWeapon;

internal sealed partial class GrowthEngine
{
    /// <summary>Find (and cache) this player's combat struct by fingerprint; guarded reads. The
    /// struct's weapon field (+0x20) holds ONE of the wielded hands (the right hand for a
    /// dual-wielder), so we match it against either hand.
    ///
    /// Scan order: player-side slots (n >= 0) first, then enemy-side (n &lt; 0), so a player
    /// is located before an enemy is even considered. Homogeneous twin tie-break: when all
    /// matching entries share the same weapon id (the unit literally appears twice in the static
    /// array because a battle restart freezes the old slot), prefer an entry with a real (non-zero)
    /// grid position; if all share the same position too, return the first deterministically.
    /// Only HETEROGENEOUS multi-matches (different weapon ids) refuse. The ambiguous-match log is
    /// throttled to once per battle per slot so a refused slot doesn't spam every ~100ms tick.</summary>
    private long LocateStruct(int slot, int level, int brave, int faith, List<(int weapon, WeaponMeta m)> hands)
    {
        if (_structForSlot.TryGetValue(slot, out long cached) && MatchesEntry(_mem, cached, level, brave, faith, hands))
            return cached;
        return LocateIn(slot, level, brave, faith, hands, LiveEntries());
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

    /// <summary>Match + tie-break + cache + throttled ambiguity log over an explicit entry list --
    /// the ONE locate path (the live caller passes <see cref="LiveEntries"/>; tests pass pinned
    /// buffers, so they exercise this exact code). Returns the located address, or 0 on refusal.</summary>
    internal long LocateIn(int slot, int level, int brave, int faith,
                           List<(int weapon, WeaponMeta m)> hands, IReadOnlyList<(bool isPlayer, long addr)> entries)
    {
        long found = ScanEntries(_mem, level, brave, faith, hands, entries, out int matchCount);
        if (found != 0)
        {
            _structForSlot[slot] = found;
            if (!_logged) { _logged = true; Log.Info($"growth: found combat struct for party slot {slot}"); }
            return found;
        }
        if (matchCount > 1 && _ambiguousLogged.Add(slot))
            Log.Info($"growth: ambiguous struct locate for party slot {slot} ({matchCount} matches) -- skipping");
        return 0;
    }

    /// <summary>True when the given slot's ambiguous-locate was already logged this battle
    /// (throttle observability for tests; cleared by ResetBattle).</summary>
    internal bool IsAmbiguousLogged(int slot) => _ambiguousLogged.Contains(slot);

    /// <summary>The scan core: player-side pass first (exits before enemy-side is considered when
    /// it matches), homogeneity tracked across matches, the twin tie-break applied to homogeneous
    /// multi-matches. Returns the pick, or 0 (with <paramref name="matchCount"/> for the caller's
    /// ambiguity log) when nothing matches or a heterogeneous multi-match refuses.</summary>
    internal static long ScanEntries(IGameMemory mem, int level, int brave, int faith,
                                     List<(int weapon, WeaponMeta m)> hands,
                                     IReadOnlyList<(bool isPlayer, long addr)> entries, out int matchCount)
    {
        long found = 0; matchCount = 0; int tieWeapon = -1; bool homogeneous = true;
        for (int pass = 0; pass < 2 && matchCount == 0; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(mem, addr, level, brave, faith, hands)) continue;
                int wid = mem.U16(addr + Offsets.CWeapon);
                if (tieWeapon == -1) tieWeapon = wid;
                else if (tieWeapon != wid) homogeneous = false;
                matchCount++; found = addr;
            }
        }
        if (matchCount == 1) return found;
        if (matchCount > 1 && homogeneous)
            return PickHomogeneousTwin(mem, level, brave, faith, hands, entries);
        return 0;
    }

    /// <summary>Convenience overload for callers that don't need the match count.</summary>
    internal static long ScanEntries(IGameMemory mem, int level, int brave, int faith,
                                     List<(int weapon, WeaponMeta m)> hands,
                                     IReadOnlyList<(bool isPlayer, long addr)> entries)
        => ScanEntries(mem, level, brave, faith, hands, entries, out _);

    /// <summary>Re-scan a homogeneous multi-match (all matches share the same weapon id) and
    /// apply the twin tie-break: prefer a real-position (non-zero gx or gy) entry; if all are at
    /// (0,0) return the first deterministically.</summary>
    private static long PickHomogeneousTwin(IGameMemory mem, int level, int brave, int faith,
                                            List<(int weapon, WeaponMeta m)> hands,
                                            IReadOnlyList<(bool isPlayer, long addr)> entries)
    {
        long first = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(mem, addr, level, brave, faith, hands)) continue;
                if (first == 0) first = addr;
                bool realPos = mem.U8(addr + Offsets.AGx) != 0 || mem.U8(addr + Offsets.AGy) != 0;
                if (realPos) return addr;
            }
        }
        return first;   // all at (0,0): return first deterministically
    }

    /// <summary>True if S is a readable combat struct matching this unit (level + brave/faith + its
    /// weapon field equals either wielded hand + sane PA/MA). Internal so tests can drive it directly
    /// via pinned buffers (a LiveMemory's RPM/WPM work on any committed address in the process).</summary>
    internal static bool MatchesEntry(IGameMemory mem, long s, int level, int brave, int faith,
                                      List<(int weapon, WeaponMeta m)> hands)
    {
        if (!mem.Readable(s, StructSpan)) return false;
        int cw = mem.U16(s + Offsets.CWeapon);
        if (!hands.Exists(x => x.weapon == cw)) return false;
        if (!Band.LevelMatchesRoster(level, mem.U8(s + Offsets.CLevel))) return false;   // level = pre-battle roster value
        if (mem.U8(s + Offsets.CBrave) != brave || mem.U8(s + Offsets.CFaith) != faith) return false;
        int pa = mem.U8(s + Offsets.CPa), ma = mem.U8(s + Offsets.CMa);
        return pa >= StatMin && pa <= SigStatHi && ma >= StatMin && ma <= SigStatHi;
    }

}
