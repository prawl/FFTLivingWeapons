#!/usr/bin/env python
"""
JobCommand table BASE re-finder (READ-ONLY -- cannot crash the game, never writes).

WHY: the FFT:IC 1.5 recompile moved every absolute address. The JobCommand table base
(Barrage.AbilityBase, pre-1.5 = 0x140679436 - 27*25 = 0x140679193) is the single most
dangerous WRITE anchor in the port -- a stale-but-valid address corrupts a real command
list. This probe re-finds it deliberately, by signature, and VERIFIES against multiple
independent records before anyone wires it in.

LAYOUT (barrage-jobcommand-injection memory): 25-byte records, record index ==
JobCommandData.xml Id. Each record = [ExtAb u16][ExtRSM u8][AbilityId1..16 u8][RSM1..6 u8].
AbilityId bytes are the low 8 bits of the ability id; the ExtAb bits add 256.

SIGNATURE: two documented anchors sit adjacent and contain ascending runs that are
near-unique in the image --
  rec 8  (Archer "Aim")          ability bytes 150..157 (0x96..0x9D)  ids 406-413
  rec 9  (Monk "Martial Arts")   ability bytes 100..107 (0x64..0x6B)  ids 100-107
They are exactly 25 bytes apart (rec8 ability1 .. rec9 ability1). Find that pair and the
base = (rec8 ability1 addr) - 8*25. Then DUMP a spread of records with names to confirm
the whole table reads coherently at the candidate base.

USAGE (game running):
  python jobcommand_find_probe.py            # find the base + verify (read-only)
  python jobcommand_find_probe.py dump <base_hex> [recLo] [recHi]   # dump records at a base
"""
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV, find_pid, k32, rd

REC = 25
PRE15_BASE = 0x140679436 - 27 * 25            # = 0x140679193 (pre-1.5 rec0 AbilityId1)
ABILITY_DB = r"C:\Users\ptyRa\Dev\FFTItemOverhaul\working\nxd_ability\ability.sqlite"

# scan window (main module: base 0x140000000, 1.5 SizeOfImage 0x190EB000)
SCAN_LO = 0x140000000
SCAN_HI = 0x142000000
CHUNK = 0x200000
OVERLAP = 0x80

REC8_SIG = bytes(range(150, 158))   # 0x96..0x9D  (Archer Aim ability bytes)
REC9_SIG = bytes(range(100, 108))   # 0x64..0x6B  (Monk Martial Arts ability bytes)

_names = None


def name(aid):
    global _names
    if _names is None:
        try:
            con = sqlite3.connect(ABILITY_DB)
            _names = {k: (n or "?") for k, n in con.execute('select Key, Name from "Ability-en"')}
            con.close()
        except Exception:
            _names = {}
    return _names.get(aid, "?")


def read_rec(h, base, rec):
    """25-byte record at <base> (rec0 ability1). Returns (ext, extrsm, ab[16], rsm[6])."""
    buf = rd(h, base + rec * REC - 3, REC)
    if not buf:
        return None
    ext = buf[0] | (buf[1] << 8)
    return ext, buf[2], list(buf[3:19]), list(buf[19:25])


def ability_id(ab_byte, ext, slot_i):
    # MSB-first per byte (proven 2026-06-10): byte0 = slots 1-8, byte1 = slots 9-16.
    bit = (7 - slot_i % 8) + 8 * (slot_i // 8)
    return ab_byte + (256 if ext & (1 << bit) else 0)


def dump_rec(h, base, rec):
    got = read_rec(h, base, rec)
    if not got:
        return f"  rec {rec:3}: unreadable"
    ext, extrsm, ab, rsm = got
    ids = [ability_id(ab[i], ext, i) for i in range(16)]
    lab = ", ".join(f"{a}:{name(a)}" for a in ids if a)
    return f"  rec {rec:3} extAb={ext:04X}: {lab or '(empty)'}"


def find_base(h):
    """Scan the module for the rec8+rec9 adjacent signature; return list of candidate bases."""
    cands = []
    addr = SCAN_LO
    while addr < SCAN_HI:
        n = min(CHUNK + OVERLAP, SCAN_HI - addr)
        buf = rd(h, addr, n)
        if buf:
            pos = buf.find(REC8_SIG)
            while pos != -1:
                # rec 9's ability1 must sit exactly 25 bytes after rec 8's ability1.
                if buf[pos + REC:pos + REC + 8] == REC9_SIG:
                    rec8_ability1 = addr + pos
                    base = rec8_ability1 - 8 * REC
                    if base not in cands:
                        cands.append(base)
                pos = buf.find(REC8_SIG, pos + 1)
        addr += CHUNK
    return cands


def verify(h, base):
    """Cross-check a candidate base against independent documented records.
    Returns (ok, list_of_check_strings)."""
    checks = []
    ok = True

    def first8(rec):
        got = read_rec(h, base, rec)
        return got[2][:8] if got else None

    # rec 8 = Aim 406-413 ; rec 9 = Martial Arts 100-107 (the locator pair)
    checks.append(("rec8 Aim bytes 150-157", first8(8) == list(REC8_SIG)))
    checks.append(("rec9 MartialArts bytes 100-107", first8(9) == list(REC9_SIG)))
    # rec 14 = Thief Steal (the Barrage host). Must be non-empty + sane ids.
    got14 = read_rec(h, base, 14)
    steal_ok = bool(got14) and any(got14[2]) and all(b < 200 for b in got14[2])
    checks.append(("rec14 Steal non-empty/sane", steal_ok))
    # rec 0 should read as a coherent record (ext flag bits plausible, not 0xFFFF garbage)
    got0 = read_rec(h, base, 0)
    rec0_ok = bool(got0) and got0[0] != 0xFFFF
    checks.append(("rec0 coherent", rec0_ok))
    for _, good in checks:
        ok = ok and good
    return ok, checks


def cmd_find():
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return
    h = k32.OpenProcess(PV, False, pid)        # READ-ONLY handle
    try:
        # sanity: confirm the pre-1.5 base is now stale (does NOT read the table)
        stale_ok, _ = verify(h, PRE15_BASE)
        print(f"pre-1.5 base 0x{PRE15_BASE:012X}: {'STILL VALID (?!)' if stale_ok else 'stale (expected on 1.5)'}\n")

        cands = find_base(h)
        if not cands:
            print("NO candidates -- signature not found. Is the game past the boot/title? "
                  "(the table is built at boot). Widen SCAN_HI if needed.")
            return
        print(f"{len(cands)} signature hit(s):\n")
        for base in cands:
            delta = base - PRE15_BASE
            ok, checks = verify(h, base)
            print(f"=== candidate base 0x{base:012X}  (delta {'+' if delta>=0 else '-'}0x{abs(delta):X}) "
                  f"-> {'VERIFIED' if ok else 'REJECTED'} ===")
            for label, good in checks:
                print(f"    [{'OK ' if good else 'XX '}] {label}")
            print("  sample records:")
            for r in (0, 5, 7, 8, 9, 11, 14, 16, 19, 22, 37, 163):
                print(dump_rec(h, base, r))
            print()
        verified = [b for b in cands if verify(h, b)[0]]
        if len(verified) == 1:
            b = verified[0]
            print(f"RESULT: unique verified base = 0x{b:012X}")
            print(f"  Barrage.AbilityBase (rec0 ability1) = 0x{b:012X}")
            print(f"  delta from pre-1.5 = +0x{b - PRE15_BASE:X}")
        elif len(verified) > 1:
            print(f"RESULT: {len(verified)} verified candidates -- inspect the dumps to disambiguate.")
        else:
            print("RESULT: no candidate passed verification. Do NOT wire anything.")
    finally:
        k32.CloseHandle(h)


def cmd_dump(base, lo, hi):
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running"); return
    h = k32.OpenProcess(PV, False, pid)
    try:
        for r in range(lo, hi):
            print(dump_rec(h, base, r))
    finally:
        k32.CloseHandle(h)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "dump":
        base = int(sys.argv[2], 16)
        lo = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        hi = int(sys.argv[4]) if len(sys.argv) > 4 else lo + 30
        cmd_dump(base, lo, hi)
    else:
        cmd_find()


if __name__ == "__main__":
    main()
