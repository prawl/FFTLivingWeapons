"""Set the TRUE extend bit for rec 8 slot 9 (per-byte MSB layout: flag byte0 = slots 1-8,
byte1 = slots 9-16, bit 0x80 = first slot of each byte). Slot 9 -> byte1 |= 0x80."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ABILITY_BASE = 0x140679436 - 27 * 25
flag = ABILITY_BASE + 8 * 25 - 3

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    b = rd(h, flag, 2)
    print(f"rec 8 ext bytes before: {b[0]:02X} {b[1]:02X}")
    wr(h, flag + 1, bytes([b[1] | 0x80]))
    b = rd(h, flag, 2)
    print(f"rec 8 ext bytes after:  {b[0]:02X} {b[1]:02X}  (slot 9 extended -> byte 102 = ability 358)")
finally:
    k32.CloseHandle(h)
