#!/usr/bin/env python
"""LW-346 READ-ONLY watcher: when does the equipped id 261 leave Ramza's hand, and the bag?

Plain language: polls a few bytes of the running game about 500 times a second and prints a
timestamped line every time one of them changes, so "the Moonblade vanished somewhere between
the formation screen and the first battle turn" becomes "it flipped at 21:40:12.345, while the
screen was X". Watches roster slot 0's four equip words (rHand at 0x1411A7D24, then the next
three), the bag count for id 261 (0x1411A7C00 + 261), and the party-block snapshot copy of the
rHand word inside the battle snapshot is not known, so it is not watched. Writes nothing.
Usage: python tools/probes/lw346_equip_watch.py [seconds]   (default 600)
"""
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32, PV  # noqa: E402

SITES = [("rHand", 0x1411A7D24, 2), ("lHand", 0x1411A7D26, 2), ("slot3", 0x1411A7D28, 2), ("slot4", 0x1411A7D2A, 2),
         ("bag[261]", 0x1411A7C00 + 261, 1), ("bag[37]", 0x1411A7C00 + 37, 1)]


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 600
    h = k32.OpenProcess(PV, False, find_pid("fft_enhanced.exe"))
    last = {}
    t0 = time.time()
    print("watching (%.0fs) ..." % secs, flush=True)
    while time.time() - t0 < secs:
        for name, a, n in SITES:
            b = rd(h, a, n)
            v = None if b is None else (b[0] if n == 1 else struct.unpack("<H", b)[0])
            if last.get(name, "unset") != v:
                print("%s  %-8s %s -> %s" % (time.strftime("%H:%M:%S") + ".%03d" % int((time.time() % 1) * 1000), name,
                                              "unset" if name not in last else ("0x%04X" % last[name] if last[name] is not None else "?"),
                                              "?" if v is None else "0x%04X" % v), flush=True)
                last[name] = v
        time.sleep(0.002)
    print("done", flush=True)


if __name__ == "__main__":
    main()
