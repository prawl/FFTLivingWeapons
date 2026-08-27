#!/usr/bin/env python
"""LW-346 live poke (outside-process WPM): let the default-order rebuilds keep id 261.

Plain language: two menus rebuild their item lists from "display order" tables of item ids
(the party Inventory tab order, and the unit equip picker's order, which the game rewrites
from the last inventory sort). Any id missing from a table is silently dropped, and the new
Moonblade (261) is missing from both because the table generators stop at id 260. This
probe appends 261 to each weapons table right where its end marker sits (then re-adds the
marker) and widens the rebuild routine's one hard-coded "stop at id 261" guard. Both tables
are filled at load time, so run this AFTER the save is loaded (a boot-time marker patch would
be overwritten); then re-open the menu.

Guards: every write is checked against its expected old bytes first (a mismatch refuses the
whole poke); a table that already lists 261 is left alone; --restore undoes only this
probe's own shape. Everything dies at game exit anyway.
Provenance: docs/research/ITEM_CAP_261_BREAK_JOURNEY.md (2026-08-27 early sections).
Also prepends 261 to the "Acquired" sort-order list (the mode-8 rebuild reads that list and
ignores the rebuild's count, which is the 21:15 crash). Usage:
  python tools/probes/lw346_order_table_poke.py --apply | --restore | --peek
"""
import ctypes as C
import struct
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402

PRW = 0x0438  # VM_READ | VM_WRITE | VM_OPERATION | QUERY_INFORMATION
NEW_ID = 0x105
END = 0x00FF
TABLES = [
    ("inventory weapons order table (pointer array 0x14067F498[0])", 0x14067F498),
    ("equip-picker weapons order table (pointer array 0x14067FA90[0])", 0x14067FA90),
]
# The "Acquired" sort order list (most recent first, 0x00FF end marker, at most 0x92 words): the
# game enrols new ids into it over ids 1..260 only, so 261 is prepended here as if just acquired.
ACQUIRED = ("acquired-order list 0x141874726", 0x141874726)
ACQ_MAX = 0x92
GUARD = ("default-order table-scan guard imm 0x105 -> 0x106 (mov eax,imm32 @0x140285E2C)", 0x140285E2D, b"\x05", b"\x06")


def wr(h, a, data):
    n = C.c_size_t(0)
    buf = (C.c_ubyte * len(data)).from_buffer_copy(data)
    return bool(k32.WriteProcessMemory(h, C.c_void_p(a), buf, len(data), C.byref(n))) and n.value == len(data)


def table_state(h, ptr_slot):
    """Return (table base, end-marker index, state) where state is vanilla / has-261 / odd."""
    base = struct.unpack("<Q", rd(h, ptr_slot, 8))[0]
    w = list(struct.unpack("<200H", rd(h, base, 400)))
    if END not in w:
        return base, None, "odd (no end marker in 200 words)"
    i = w.index(END)
    if NEW_ID in w[:i]:
        return base, i, "has-261"
    if w[i + 1] != 0:
        return base, i, "odd (word after the end marker is not zero padding)"
    return base, i, "vanilla"


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "--peek"
    h = k32.OpenProcess(PRW, False, find_pid("fft_enhanced.exe"))
    if not h:
        print("OpenProcess failed", C.get_last_error()); return 2
    hx = lambda b: " ".join("%02X" % x for x in b)
    plan = []
    for label, slot in TABLES:
        base, i, st = table_state(h, slot)
        print("%s @0x%X: %s, end marker at index %s" % (label, base, st, i))
        if mode == "--apply" and st == "vanilla":
            plan.append((label, base + 2 * i, b"\xFF\x00\x00\x00", struct.pack("<HH", NEW_ID, END)))
        if mode == "--restore" and st == "has-261" and i >= 1:
            a = base + 2 * (i - 1)
            if struct.unpack("<HH", rd(h, a, 4)) == (NEW_ID, END):  # our shape: [..., 261, END]
                plan.append((label, a, struct.pack("<HH", NEW_ID, END), b"\xFF\x00\x00\x00"))
    alabel, abase = ACQUIRED
    aw = list(struct.unpack("<%dH" % (ACQ_MAX + 2), rd(h, abase, 2 * (ACQ_MAX + 2))))
    aend = next((k for k, x in enumerate(aw) if x == END or x >= 0x106), None)
    ahas = aend is not None and NEW_ID in aw[:aend]
    print("%s: %s entries, end marker at %s, has-261 %s" % (alabel, aend, aend, ahas))
    if mode == "--apply" and aend is not None and not ahas and aend + 1 < ACQ_MAX and aw[aend] == END and aw[aend + 1] == 0:
        old = struct.pack("<%dH" % (aend + 2), *aw[:aend + 2])
        new = struct.pack("<%dH" % (aend + 2), NEW_ID, *aw[:aend + 1])
        plan.append((alabel + " (prepend 261)", abase, old, new))
    if mode == "--restore" and aend is not None and ahas and aw[0] == NEW_ID:
        old = struct.pack("<%dH" % (aend + 1), *aw[:aend + 1])
        new = struct.pack("<%dH" % (aend + 1), *aw[1:aend + 1], 0)
        plan.append((alabel + " (drop the leading 261)", abase, old, new))
    glabel, ga, gold, gnew = GUARD
    gcur = rd(h, ga, 1)
    print("%s: reads %s" % (glabel, hx(gcur)))
    if mode == "--apply" and gcur == gold:
        plan.append((glabel, ga, gold, gnew))
    if mode == "--restore" and gcur == gnew:
        plan.append((glabel, ga, gnew, gold))
    if mode == "--peek":
        return 0
    if not plan:
        print("nothing to do"); return 0
    for label, a, old, new in plan:
        cur = rd(h, a, len(old))
        if cur != old:
            print("REFUSED: 0x%X reads %s, expected %s (%s)" % (a, hx(cur), hx(old), label)); return 1
    for label, a, old, new in plan:
        ok = wr(h, a, new); back = rd(h, a, len(new))
        print("%s 0x%X -> %s : %s  (%s)" % ("wrote" if ok else "WRITE FAILED", a, hx(new), "verified" if back == new else "READBACK " + hx(back), label))
    return 0


if __name__ == "__main__":
    sys.exit(main())
