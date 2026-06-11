#!/usr/bin/env python
"""
Barrage probe -- inject ability 358 (Barrage, vanilla-unused 4x-attack) into a live
JobCommand record and see if it shows/casts in the command menu.

VERIFIED LAYOUT (modloader JOB_COMMAND_DATA.cs + live anchors 2026-06-09): 25-byte records,
fields [ExtAbilityFlags u16][ExtRSMFlags u8][AbilityId1..16 u8][RSMId1..6 u8].
ABILITY_BASE (0x140679436 - 27*25) points at record 0's AbilityId1, so a record's flag
bytes sit at ABILITY_BASE + rec*25 - 3. Record index == JobCommandData.xml Id.
Anchors: rec 9 = Monk Martial Arts (100..107), rec 37/38/68 = Machinist (213/214/215),
rec 163 = black-magic subset (16/17/20/24). ExtRSM is MSB-first (bit7 = RSM1) -- proven by
RSM-count correlation; ExtAbility assumed MSB-first too (bit15 = AbilityId1; NO natural
example exists, every vanilla record uses ids < 256). If an injected >=256 id renders as
its low byte's ability, rerun inject with lsb as the final arg to flip the bit order.

USAGE:
  python barrage_probe.py dump <recId> [count=1]
  python barrage_probe.py scan <abilityId>
  python barrage_probe.py inject <recId> <slot1to16> [abilityId=358] [msb|lsb]
  python barrage_probe.py restore <recId>
RPM/WPM only -- cannot crash the game.
"""
import json
import os
import sqlite3
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ABILITY_BASE = 0x140679436 - 27 * 25
REC = 25
NREC = 200
UNDO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "barrage_undo.json")
ABILITY_DB = r"C:\Users\ptyRa\Dev\FFTItemOverhaul\working\nxd_ability\ability.sqlite"

_names = None


def name(aid):
    global _names
    if _names is None:
        con = sqlite3.connect(ABILITY_DB)
        _names = {k: (n or "?") for k, n in con.execute('select Key, Name from "Ability-en"')}
        con.close()
    return _names.get(aid, "?")


def read_rec(h, rec):
    """Full 25-byte record INCLUDING its flag prefix. Returns (ext_u16, extrsm, ab[16], rsm[6])."""
    buf = rd(h, ABILITY_BASE + rec * REC - 3, REC)
    if not buf:
        return None
    ext = buf[0] | (buf[1] << 8)
    return ext, buf[2], list(buf[3:19]), list(buf[19:25])


def ability_id(ab_byte, ext, slot_i, order="msb"):
    # LIVE-PROVEN 2026-06-10: extend bits are MSB-first PER BYTE (byte0 = slots 1-8,
    # byte1 = slots 9-16; as LE-composed u16, slot 10 = 0x4000). The old whole-u16
    # order (15 - slot_i) rendered injected 358 as Aurablast and produced the ghost
    # "Vengeance" dump artifacts. "msb" now means the proven per-byte order.
    bit = ((7 - slot_i % 8) + 8 * (slot_i // 8)) if order == "msb" else slot_i
    return ab_byte + (256 if ext & (1 << bit) else 0)


def cmd_dump(h, rec, count):
    for r in range(rec, rec + count):
        got = read_rec(h, r)
        if not got:
            print(f"rec {r}: unreadable")
            continue
        ext, extrsm, ab, rsm = got
        ids = [ability_id(ab[i], ext, i) for i in range(16)]
        lab = ", ".join(f"{a}:{name(a)}" for a in ids if a)
        print(f"rec {r:3} extAb={ext:04X} extRSM={extrsm:02X} abilities: {lab or '(empty)'}")
        if any(rsm):
            shown = [v + (256 if extrsm & (0x80 >> i) else 0) for i, v in enumerate(rsm) if v]
            print(f"        rsm: {[f'{v}:{name(v)}' for v in shown]}")


def cmd_scan(h, aid):
    hits = []
    for r in range(NREC):
        got = read_rec(h, r)
        if not got:
            continue
        ext, _, ab, _ = got
        for i in range(16):
            if ab[i] == (aid & 0xFF) and ability_id(ab[i], ext, i) == aid:
                hits.append((r, i + 1))
    print(f"ability {aid} ({name(aid)}) found in:", hits or "nowhere")


def cmd_inject(h, rec, slot, aid, order):
    flag_addr = ABILITY_BASE + rec * REC - 3
    got = read_rec(h, rec)
    if not got:
        print("record unreadable")
        return
    ext, extrsm, ab, rsm = got
    undo = json.load(open(UNDO)) if os.path.exists(UNDO) else {}
    undo.setdefault(str(rec), list(rd(h, flag_addr, REC)))   # FIRST baseline, flags included
    json.dump(undo, open(UNDO, "w"))
    i = slot - 1
    print(f"rec {rec} slot {slot} before: {ability_id(ab[i], ext, i, order)} "
          f"({name(ability_id(ab[i], ext, i, order))})")
    wr(h, ABILITY_BASE + rec * REC + i, bytes([aid & 0xFF]))
    bit = ((7 - i % 8) + 8 * (i // 8)) if order == "msb" else i
    ext = (ext | (1 << bit)) if aid >= 256 else (ext & ~(1 << bit))
    wr(h, flag_addr, bytes([ext & 0xFF, (ext >> 8) & 0xFF]))
    got2 = read_rec(h, rec)
    ext2, _, ab2, _ = got2
    print(f"rec {rec} slot {slot} now:    {ability_id(ab2[i], ext2, i, order)} "
          f"({name(ability_id(ab2[i], ext2, i, order))})  [extAb={ext2:04X}, order={order}]")
    print(f"baseline saved -- 'restore {rec}' reverts.")


def cmd_restore(h, rec):
    undo = json.load(open(UNDO))
    wr(h, ABILITY_BASE + rec * REC - 3, bytes(undo[str(rec)]))
    print(f"rec {rec} restored ({REC} bytes incl. flags).")


def main():
    a = sys.argv
    mode = a[1] if len(a) > 1 else ""
    if mode not in ("dump", "scan", "inject", "restore"):
        print(__doc__)
        return
    pid = find_pid(PROC)
    if not pid:
        print(f"{PROC} not running")
        return
    h = k32.OpenProcess(PV_W, False, pid)
    try:
        if mode == "dump":
            cmd_dump(h, int(a[2]), int(a[3]) if len(a) > 3 else 1)
        elif mode == "scan":
            cmd_scan(h, int(a[2]))
        elif mode == "inject":
            cmd_inject(h, int(a[2]), int(a[3]),
                       int(a[4]) if len(a) > 4 else 358,
                       a[5] if len(a) > 5 else "msb")
        else:
            cmd_restore(h, int(a[2]))
    finally:
        k32.CloseHandle(h)


if __name__ == "__main__":
    main()
