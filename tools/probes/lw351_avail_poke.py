#!/usr/bin/env python
"""LW-351 stage-1 P6 probe: flip the Terrastaff's shop window byte live, then restore it.

Plain language: proves the shop's chapter gate really reads our new item's window. The mod
ships Terrastaff (id 262) with the Chapter 1 window (value 4), which the owner's save has
passed, so it shows in its towns' shops. This probe pokes that ONE byte to 20 (a gate no save
has passed); re-entering the shop must then HIDE the Terrastaff. `--restore` puts the byte
back; re-entering must SHOW it again. Absent-then-present is the binary proof.

The catalog page is allocated fresh per launch: its address is re-derived every run from the
game's own redirected read at 0x1402B8C6A (disp32 -> page; vanilla reads 10 F9 67 00). The
window byte lives at page + 262*12 + 10. Every poke is recorded in
tools/probes/lw351_avail_poke_undo.json before it lands.

Usage (game running, NEW build deployed):
  python tools/probes/lw351_avail_poke.py            # read + report, no write
  python tools/probes/lw351_avail_poke.py --block    # 4 -> 20 (records undo)
  python tools/probes/lw351_avail_poke.py --restore  # undo file -> original value
"""
import ctypes as C
import json
import os
import struct
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, k32  # noqa: E402

REDIRECT_DISP32_ADDR = 0x1402B8C6A   # the catalog getter's disp32 (redirected by the mod)
VANILLA_DISP32 = 0x0067F910          # what the byte reads UNMODDED; seeing this = mod not armed
ITEM_ID, RECORD_STRIDE, AVAIL_OFF = 262, 12, 10
BLOCK_VALUE = 20                     # Unknown20: a story gate no save has passed
UNDO = os.path.join(os.path.dirname(__file__), "lw351_avail_poke_undo.json")


def rd(h, a, n):
    b = (C.c_ubyte * n)()
    g = C.c_size_t()
    return bytes(b[:g.value]) if k32.ReadProcessMemory(h, C.c_void_p(a), b, n, C.byref(g)) else None


def wr(h, a, data):
    g = C.c_size_t()
    return bool(k32.WriteProcessMemory(h, C.c_void_p(a), bytes(data), len(data), C.byref(g))) and g.value == len(data)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "read"
    h = k32.OpenProcess(0x0438, False, find_pid("FFT_enhanced.exe"))  # +VM_WRITE|VM_OPERATION
    raw = rd(h, REDIRECT_DISP32_ADDR, 4)
    if not raw:
        sys.exit("cannot read the redirect site; is the game running?")
    disp = struct.unpack("<i", raw)[0]
    if disp == VANILLA_DISP32:
        sys.exit("redirect site still VANILLA (0x67F910): the mod's catalog page is not armed; launch the NEW build first")
    # The catalog read is [reg+imageBase+disp32] with disp32 SIGNED: page = 0x140000000 + disp
    # (the Phase-2 live read: disp 0xD4AA0000 = -0x2B560000 -> page 0x114AA0000). Sanity-check
    # via id 261's record: typeFlags byte (+7) should be 3 and price (+8) 10.
    page = 0x140000000 + disp
    addr = page + ITEM_ID * RECORD_STRIDE + AVAIL_OFF
    probe261 = rd(h, page + 261 * RECORD_STRIDE, RECORD_STRIDE)
    print("disp32=0x%X page=0x%X; id261 record: %s" % (disp & 0xFFFFFFFF, page, probe261.hex(" ") if probe261 else "unreadable"))
    cur = rd(h, addr, 1)
    if cur is None:
        sys.exit("window byte unreadable at 0x%X; the page derivation is wrong, STOP" % addr)
    print("id262 window byte @0x%X = %d" % (addr, cur[0]))
    if mode == "--block":
        json.dump({"addr": addr, "old": cur[0], "new": BLOCK_VALUE}, open(UNDO, "w"), indent=1)
        ok = wr(h, addr, [BLOCK_VALUE])
        print("poked -> %d (%s); undo recorded at %s" % (BLOCK_VALUE, "OK" if ok else "WRITE REFUSED", UNDO))
    elif mode == "--restore":
        u = json.load(open(UNDO))
        ok = wr(h, u["addr"], [u["old"]])
        print("restored @0x%X -> %d (%s)" % (u["addr"], u["old"], "OK" if ok else "WRITE REFUSED"))
    else:
        print("read-only pass complete (use --block / --restore for the P6 halves)")


if __name__ == "__main__":
    main()
