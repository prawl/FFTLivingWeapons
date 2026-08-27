#!/usr/bin/env python
"""LW-346 read-only watch: log every change of the attacker render cluster during one action.

Plain language: the game keeps a small block of "who is attacking with what" values that the
animation code reads while an action plays. This probe polls that block about a thousand
times a second and prints each change with a timestamp, so the order of writes during one
swing (which hand was chosen, which weapon id was published, what the derived "weapon shown"
word became) is visible without a debugger. Writes nothing.

Cluster (1.5.2, docs/research/ITEM_CAP_261_BREAK_JOURNEY.md 2026-08-27 sections):
  0x1407B0762 flag word (low byte hand mode, high byte selects the OFF hand when nonzero in
              the attack setup 0x1403099B0), 0x1407B0764 main-hand id, 0x1407B0766 off-hand id
  (pub2 0x14F45D298 / plain publisher 0x140282F88), 0x1407B077A derived "weapon shown" id
  (0 = unarmed; writers 0x140309B75 and 0x1506E81F3), 0x1407B07A8 8-byte weapon-stat staging.
Usage: python tools/probes/lw346_render_cluster_watch.py [seconds=20] [combat struct addr, e.g. 0x141855CE0]
"""
import struct
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0] if "\\" in __file__ else ".")
from lw346_live_disasm import find_pid, rd, k32  # noqa: E402

BASE = 0x1407B0760
SIZE = 0x60
OBJ_TABLE = 0x141800F50  # battle object pointer table; slot 0 = the first player unit (Ramza)
ACTION_OFF = 0x1A0        # 0x14-byte action block the attack setup copies (+1 type, +2 word, +8 item/weapon id)
ACTION_LEN = 0x20


def fields(b):
    flag, main, off = struct.unpack_from("<HHH", b, 2)
    derived = struct.unpack_from("<H", b, 0x1A)[0]
    return "flag=%04X main=%d off=%d derived=%d" % (flag, main, off, derived)


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
    h = k32.OpenProcess(0x0010 | 0x0400, False, find_pid("fft_enhanced.exe"))
    obj = int(sys.argv[2], 0) if len(sys.argv) > 2 else struct.unpack("<Q", rd(h, OBJ_TABLE, 8))[0]
    act = obj + ACTION_OFF if obj else None
    prev = rd(h, BASE, SIZE)
    prev_a = rd(h, act, ACTION_LEN) if act else b""
    t0 = time.perf_counter()
    print("start  %s  struct=0x%X action=%s" % (fields(prev), obj, prev_a.hex()))
    n = 0
    while time.perf_counter() - t0 < secs:
        cur = rd(h, BASE, SIZE)
        if cur and cur != prev:
            n += 1
            diff = " ".join("+%02X:%02X>%02X" % (i, prev[i], cur[i]) for i in range(SIZE) if prev[i] != cur[i])
            print("%8.3fs  %s  | %s" % (time.perf_counter() - t0, fields(cur), diff))
            prev = cur
        if act:
            cur_a = rd(h, act, ACTION_LEN)
            if cur_a and cur_a != prev_a:
                n += 1
                print("%8.3fs  ACTION obj0+0x1A0: %s  (type=%d w2=%d w8=%d)" % (time.perf_counter() - t0, cur_a.hex(" "), cur_a[1], struct.unpack_from("<H", cur_a, 2)[0], struct.unpack_from("<H", cur_a, 8)[0]))
                prev_a = cur_a
        time.sleep(0.0005)
    print("end    %s  (%d changes)" % (fields(prev), n))
    return 0


if __name__ == "__main__":
    sys.exit(main())
