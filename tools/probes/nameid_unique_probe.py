#!/usr/bin/env python
"""
NameId-uniqueness probe (READ-ONLY). Phase 0 premise check for the wrong-unit identity fix
(player report 2026-08-17: kills credited to the wrong weapon, Dual Wield leaking onto the
wrong unit; handoff section 1). The fix wants ONE truly unique join key between a roster row
and its live band seat. The proven candidate is the roster nameId (LIVE_LEDGER
[frame-1fc-nameid-mirror]: frame +0x1FC mirrors it, exact for player seats). What has never
been proven is that party nameIds cannot COLLIDE. Two falsifiable premises, one verb each:

  P1 `roster` (game running, save loaded; no battle needed):
     "Every occupied roster row carries a NONZERO nameId that is UNIQUE among occupied rows."
     PASS  -> the verdict line reads `P1 PASS`: nameId can serve as the party-unique key.
     FAIL  -> `P1 FAIL` names the colliding/zero rows: nameId alone cannot be the key; the
              fix design escalates to a composite key or a back-index hunt.
     Bonus: prints (lvl,brave,faith) fingerprint collisions among occupied rows -- the exact
     ambiguity class behind the player report (informational, not part of P1).

  P2 `battle` (run DURING a battle; run it twice a few minutes apart to also see drift):
     "Every deployed real-position band seat whose frame nameId is nonzero maps to EXACTLY
      ONE occupied roster row (players), or to NO row (enemies); no player-looking seat
      reads nameId 0."
     PASS  -> `P2 PASS`: the mirror covers every deployed player seat right now.
     FAIL shapes (each printed explicitly): a seat matching a roster fingerprint+weapon but
     reading nameId 0 (mirror unseeded -> tier-1 unusable that moment); a nameId matching
     2+ roster rows (duplicate leak into battle); drift between two runs.

Addresses come from LivingWeapon/Offsets.cs via tools/lib/offsets.py (never hardcoded).
Usage:  python tools/probes/nameid_unique_probe.py roster
        python tools/probes/nameid_unique_probe.py battle
"""
import ctypes
import ctypes.wintypes as W
import pathlib
import struct
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from lib import offsets as _offsets

O = _offsets.load()
(ROSTER_BASE, ROSTER_STRIDE, ROSTER_SLOTS, R_RHAND, R_LHAND, R_OFFHAND,
 R_LEVEL, R_BRAVE, R_FAITH, R_NAMEID) = _offsets.require(
    ["RosterBase", "RosterStride", "RosterSlots", "RRHand", "RLHand", "ROffHand",
     "RLevel", "RBrave", "RFaith", "RNameId"], O)
(COMBAT_ANCHOR, BAND_ENTRY, C_WEAPON, BAND_SLOTS, STRIDE, A_LEVEL, A_BRAVE,
 A_FAITH, A_HP, A_MAXHP, A_GX, A_GY, A_NAMEID) = _offsets.require(
    ["CombatAnchor", "BandEntry", "CWeapon", "BandSlots", "CombatStride", "ALevel",
     "ABrave", "AFaith", "AHp", "AMaxHp", "AGx", "AGy", "ANameId"], O)
# Computed constants (the parser only sees literals): same algebra as Offsets.cs.
BAND_BASE = COMBAT_ANCHOR + BAND_ENTRY - 24 * STRIDE   # == Offsets.BandReadBase (n=-24 anchor)
A_WEAPON = C_WEAPON - BAND_ENTRY                       # == Offsets.AWeapon (0x04)

PROCESS_VM_READ = 0x0010
PROCESS_QUERY_INFORMATION = 0x0400
k32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi


def open_game():
    arr = (W.DWORD * 4096)()
    needed = W.DWORD()
    psapi.EnumProcesses(ctypes.byref(arr), ctypes.sizeof(arr), ctypes.byref(needed))
    for i in range(needed.value // ctypes.sizeof(W.DWORD)):
        h = k32.OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, False, arr[i])
        if not h:
            continue
        buf = ctypes.create_unicode_buffer(260)
        # case-insensitive on purpose: the live image is FFT_enhanced.exe (capital FFT)
        if psapi.GetModuleBaseNameW(h, None, buf, 260) and buf.value.lower() == "fft_enhanced.exe":
            return h
        k32.CloseHandle(h)
    return None


H = open_game()
if not H:
    print("game not running (fft_enhanced.exe not found)")
    sys.exit(1)


def rpm(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(H, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def u16(data, off):
    return struct.unpack_from("<H", data, off)[0]


def occupied_roster():
    """[(slot, nameId, lvl, br, fa, rh, lh, oh)] for rows with level 1..99 (TryOccupiedSlot rule)."""
    rows = []
    for s in range(ROSTER_SLOTS):
        d = rpm(ROSTER_BASE + s * ROSTER_STRIDE, R_NAMEID + 2)
        if d is None:
            continue
        lvl = d[R_LEVEL]
        if not (1 <= lvl <= 99):
            continue
        rows.append((s, u16(d, R_NAMEID), lvl, d[R_BRAVE], d[R_FAITH],
                     u16(d, R_RHAND), u16(d, R_LHAND), u16(d, R_OFFHAND)))
    return rows


def verb_roster():
    rows = occupied_roster()
    print(f"{'slot':>4} {'nameId':>6} {'lvl':>3} {'br':>3} {'fa':>3} {'rh':>5} {'lh':>5} {'oh':>5}")
    for (s, nm, lvl, br, fa, rh, lh, oh) in rows:
        print(f"{s:>4} {nm:>6} {lvl:>3} {br:>3} {fa:>3} {rh:>5} {lh:>5} {oh:>5}")
    print(f"\noccupied rows: {len(rows)}")

    zeros = [r for r in rows if r[1] == 0]
    by_name = {}
    for r in rows:
        by_name.setdefault(r[1], []).append(r[0])
    dups = {nm: slots for nm, slots in by_name.items() if nm != 0 and len(slots) > 1}
    if not zeros and not dups:
        print("P1 PASS: every occupied row has a nonzero nameId, all distinct")
    else:
        print("P1 FAIL:")
        for r in zeros:
            print(f"  slot {r[0]} nameId reads 0")
        for nm, slots in sorted(dups.items()):
            print(f"  nameId {nm} shared by roster slots {slots}")

    by_fp = {}
    for r in rows:
        by_fp.setdefault((r[2], r[3], r[4]), []).append(r[0])
    fp_dups = {fp: slots for fp, slots in by_fp.items() if len(slots) > 1}
    if fp_dups:
        print("bonus: (lvl,br,fa) fingerprint COLLISIONS among occupied rows (the bug's fuel):")
        for fp, slots in sorted(fp_dups.items()):
            print(f"  fp {fp} shared by roster slots {slots}")
    else:
        print("bonus: no (lvl,br,fa) fingerprint collisions in this roster right now")


def verb_battle():
    rows = occupied_roster()
    roster_by_name = {}
    for r in rows:
        roster_by_name.setdefault(r[1], []).append(r)

    print(f"{'seat':>4} {'nameId':>6} {'lvl':>3} {'br':>3} {'fa':>3} {'wpn':>5} "
          f"{'hp':>5} {'mhp':>5} {'pos':>7}  verdict")
    fails = []
    seats_seen = 0
    for s in range(BAND_SLOTS):
        e = BAND_BASE + s * STRIDE
        d = rpm(e, A_NAMEID + 2)
        if d is None:
            continue
        lvl = d[A_LEVEL]
        hp, mhp = u16(d, A_HP), u16(d, A_MAXHP)
        if not (1 <= lvl <= 99) or mhp == 0 or mhp >= 2000:
            continue   # not a plausible unit seat
        gx, gy = d[A_GX], d[A_GY]
        nm = u16(d, A_NAMEID)
        br, fa = d[A_BRAVE], d[A_FAITH]
        wpn = u16(d, A_WEAPON)
        seats_seen += 1

        hits = roster_by_name.get(nm, [])
        looks_player = any(r[2] == lvl and r[3] == br and r[4] == fa for r in rows)
        if nm == 0:
            verdict = "nameId 0" + (" on a PLAYER-shaped seat <- P2 FAIL" if looks_player else " (fine if not a player)")
            if looks_player:
                fails.append((s, verdict))
        elif len(hits) == 1:
            verdict = f"player, roster slot {hits[0][0]}"
        elif len(hits) > 1:
            verdict = f"nameId matches roster slots {[h[0] for h in hits]} <- P2 FAIL (duplicate)"
            fails.append((s, verdict))
        else:
            verdict = "not in roster (enemy/guest)"
        pos = f"({gx},{gy})"
        print(f"{s:>4} {nm:>6} {lvl:>3} {br:>3} {fa:>3} {wpn:>5} {hp:>5} {mhp:>5} {pos:>7}  {verdict}")

    print(f"\nplausible seats: {seats_seen}")
    if seats_seen == 0:
        print("P2 INCONCLUSIVE: no plausible seats read (is a battle actually loaded?)")
    elif not fails:
        print("P2 PASS: every player-shaped seat carries a nonzero nameId mapping to exactly one roster row")
    else:
        print("P2 FAIL:")
        for s, v in fails:
            print(f"  seat {s}: {v}")


def main():
    verb = sys.argv[1] if len(sys.argv) > 1 else ""
    if verb == "roster":
        verb_roster()
    elif verb == "battle":
        verb_battle()
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
