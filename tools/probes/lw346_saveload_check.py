#!/usr/bin/env python
"""LW-346 read-only probe: does the new weapon (id 261) survive a save and load?

Plain language: prints, in one line each, everything a save/load round trip could lose for
the Moonblade: is it still in Ramza's hand, is its bag count still there, do the two menu
display-order tables and the "Acquired" list still know it. Run it three times: before
saving, after the load, and after the first menu open post-load. Writes nothing.

Facts behind it (1.5.2, read live 2026-08-27; docs/research/ITEM_CAP_261_BREAK_JOURNEY.md):
  roster slot 0 0x1411A7D10: rHand u16 +0x14, lHand +0x16 (0x00FF = empty)
  bag count array 0x1411A7C00[id]; the save struct stores exactly 261 bytes of it
  (serialize 0x14021926C -> save+0x83A8, restore 0x14021B1D5 / 0x14021E1D1), so count[261]
  is NOT in the save file; the rig re-seeds it at boot only.
  order tables: inventory 0x14067F498[0], picker 0x14067FA90[0]; acquired list 0x141874726.
Usage: python tools/probes/lw346_saveload_check.py
"""
import struct
import sys

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402
from lw346_order_table_poke import table_state, TABLES, ACQUIRED, ACQ_MAX, NEW_ID, END  # noqa: E402

ROSTER0 = 0x1411A7D10
BAG = 0x1411A7C00


def main():
    h = k32.OpenProcess(0x0010 | 0x0400, False, find_pid("fft_enhanced.exe"))
    r, l = struct.unpack("<HH", rd(h, ROSTER0 + 0x14, 4))
    print("roster slot 0: rHand=%d (0x%04X) lHand=%d (0x%04X)  -> %s" % (r, r, l, l, "HOLDS 261" if NEW_ID in (r, l) else "no 261"))
    c = rd(h, BAG + NEW_ID, 1)[0]
    print("bag count[261] = %d  -> %s" % (c, "present" if c else "none (0 is a valid per-save count since LW-353; the rig-era re-seed no longer applies)"))
    for label, slot in TABLES:
        base, end, state = table_state(h, slot)
        print("%s: %s (base 0x%X, end marker at %s)" % (label, state, base, end))
    w = list(struct.unpack("<%dH" % ACQ_MAX, rd(h, ACQUIRED[1], ACQ_MAX * 2)))
    n = w.index(END) if END in w else ACQ_MAX
    print("%s: %d entries, 261 %s, first 8 = %s" % (ACQUIRED[0], n, "listed at %d" % w.index(NEW_ID) if NEW_ID in w[:n] else "ABSENT", w[:8]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
