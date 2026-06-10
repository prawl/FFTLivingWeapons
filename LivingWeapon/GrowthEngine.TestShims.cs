using System.Collections.Generic;

namespace LivingWeapon;

internal sealed partial class GrowthEngine
{
    /// <summary>Test shim: evaluate the scan/match/ambiguity logic against a caller-supplied set of
    /// (isPlayer, addr) pairs instead of the live combat anchor. Player-side entries are tried first.
    /// Applies the homogeneous twin tie-break: when all matches share the same weapon id, prefer an
    /// entry with a real (non-zero) grid position (read from buf[AGx]/buf[AGy]); refuses (returns 0)
    /// only for heterogeneous multi-matches (different weapon ids).</summary>
    internal static long ScanStructEntries(int level, int brave, int faith,
        List<(int weapon, WeaponMeta m)> hands, (bool isPlayer, long addr)[] entries)
    {
        long found = 0; int matchCount = 0; int tieWeapon = -1; bool homogeneous = true;
        for (int pass = 0; pass < 2 && matchCount == 0; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(addr, level, brave, faith, hands)) continue;
                int wid = Mem.U16(addr + Offsets.CWeapon);
                if (tieWeapon == -1) tieWeapon = wid;
                else if (tieWeapon != wid) homogeneous = false;
                matchCount++; found = addr;
            }
        }
        if (matchCount == 1) return found;
        if (matchCount > 1 && homogeneous)
            return PickHomogeneousTwinFromEntries(level, brave, faith, hands, entries);
        return 0;
    }

    /// <summary>Apply the position-preference tie-break over a test-supplied entry list (player-side
    /// first). Prefers a real-position entry; falls back to first match when all are at (0,0).</summary>
    private static long PickHomogeneousTwinFromEntries(int level, int brave, int faith,
        List<(int weapon, WeaponMeta m)> hands, (bool isPlayer, long addr)[] entries)
    {
        long first = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(addr, level, brave, faith, hands)) continue;
                if (first == 0) first = addr;
                bool realPos = Mem.U8(addr + Offsets.AGx) != 0 || Mem.U8(addr + Offsets.AGy) != 0;
                if (realPos) return addr;
            }
        }
        return first;
    }

    /// <summary>Drive the throttled ambiguous-locate path from a test-supplied entry list.
    /// Emulates what LocateStruct does (match → homogeneous tie-break or throttled log),
    /// but against pinned test memory rather than the live combat anchor. Returns the located
    /// address, or 0 on refusal.</summary>
    internal long ScanWithThrottle(int slot, int level, int brave, int faith,
        List<(int weapon, WeaponMeta m)> hands, (bool isPlayer, long addr)[] entries)
    {
        long found = ScanStructEntries(level, brave, faith, hands, entries);
        if (found != 0) { _structForSlot[slot] = found; return found; }
        // Count matches for the log (ScanStructEntries already applied tie-break; here we need
        // the raw count to decide whether to throttle-log).
        int matchCount = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantPlayer = pass == 0;
            foreach (var (isPlayer, addr) in entries)
            {
                if (isPlayer != wantPlayer) continue;
                if (!MatchesEntry(addr, level, brave, faith, hands)) continue;
                matchCount++;
            }
        }
        if (matchCount > 1 && _ambiguousLogged.Add(slot))
            Log.Info($"growth: ambiguous struct locate for party slot {slot} ({matchCount} matches) -- skipping");
        return 0;
    }

    /// <summary>Test accessor: true when the given slot's ambiguous-locate was already logged this
    /// battle. Used by throttle-behavior tests.</summary>
    internal bool IsAmbiguousLogged(int slot) => _ambiguousLogged.Contains(slot);

    /// <summary>True when the roster slot at <paramref name="rosterBase"/> has <paramref name="weaponId"/>
    /// as its RRHand weapon. Used to confirm the main-hand-only signature routing. Mem (RPM/WPM on our
    /// own process) reads any pinned test buffer passed as the address.</summary>
    internal static bool IsSignatureMainHand(long rosterBase, int weaponId)
        => Mem.U16(rosterBase + Offsets.RRHand) == weaponId;
}
