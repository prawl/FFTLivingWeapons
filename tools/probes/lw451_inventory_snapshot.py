#!/usr/bin/env python
"""READ ONLY. Snapshot of the party inventory list in the running game, for LW-451 (the three
books missing from the Type sorted Inventory list, 2026-10-18).

Plain language: with the Items screen open, print what the list buffer actually holds right now,
which sort mode the menu is in, and how many rows the list widget was told to draw. Run it once
with the list sorted by Type and once sorted by Acquired; the two prints say whether the books
(ids 95, 96, 97, the Omnilex is 98) left the buffer or only the screen. Writes nothing.

Same pointer chain as tools/probes/lw346_inventory_snapshot.py (0x14036B221 / 0x14036B1F2 in the
list build consumer): [0x143CD9DA8] -> ui root; +0x10; +0x140 -> menu; +0x70 -> list panel (NULL
when the Items screen is closed); panel+0x58 = list widget; panel+0xB18 and +0xB1C = sort modes.
Every address is a preferred base absolute rebased through lib.livebase (LW-389).

    python tools/probes/lw451_inventory_snapshot.py
"""
import ctypes
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from lib.livebase import open_game, rebase  # noqa: E402

WORD_BUF = 0x141811470   # Offsets.MenuListBuffer
UI_ROOT = 0x143CD9DA8
WATCH = (95, 96, 97, 98)

_k32 = ctypes.windll.kernel32
_k32.ReadProcessMemory.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t,
                                   ctypes.POINTER(ctypes.c_size_t)]


def rd(h, addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    ok = _k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
    return buf.raw[:got.value] if ok else b""


def q(h, addr):
    b = rd(h, addr, 8)
    return struct.unpack("<Q", b)[0] if len(b) == 8 else 0


def main():
    pid, h, base = open_game()
    print("pid %d, image base 0x%X" % (pid, base))
    raw = rd(h, rebase(WORD_BUF, base), 600)
    words = list(struct.unpack("<%dH" % (len(raw) // 2), raw))
    terms = [i for i, x in enumerate(words) if x == 0xFFFF]
    n = terms[0] if terms else len(words)
    ids = [x & 0x3FF for x in words[:n]]
    print("list buffer: %d entries, terminator at %s" % (n, terms[:3]))
    print("  books present: %s  (positions %s)" % ([i for i in WATCH if i in ids],
                                                   {i: ids.index(i) for i in WATCH if i in ids}))
    print("  extended ids: %s  duplicates: %s" % ([i for i in ids if i >= 256],
                                                   sorted({x for x in ids if ids.count(x) > 1})))
    badged = [(k, w & 0x3FF, hex(w)) for k, w in enumerate(words[:n]) if w & 0xFC00]
    print("  words with flag bits (E badge is bit 14): %s" % badged)
    print("  order: %s" % ids)
    root = q(h, rebase(UI_ROOT, base))
    a = q(h, root + 0x10) if root else 0
    menu = q(h, a + 0x140) if a else 0
    panel = q(h, menu + 0x70) if menu else 0
    print("ui chain: root 0x%X -> 0x%X -> menu 0x%X -> list panel 0x%X" % (root, a, menu, panel))
    if not panel:
        print("  list panel is NULL: the Items screen is not open; open it and run again.")
        return
    b = rd(h, panel + 0xB00, 0x30)
    if len(b) == 0x30:
        print("  sort modes: +0xB18=%d +0xB1C=%d +0xB20=%d" % struct.unpack_from("<iii", b, 0x18))
    wd = rd(h, panel + 0x58, 0x120)
    dw = struct.unpack("<%dI" % (len(wd) // 4), wd)
    print("  widget dwords that could be the row count (50..300): %s" %
          ["+0x%X=%d" % (i * 4, v) for i, v in enumerate(dw) if 50 <= v <= 300])


if __name__ == "__main__":
    main()
