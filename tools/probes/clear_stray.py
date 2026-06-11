"""Clear the stray learned-bit the buggy Barrage DLL OR'd into Ramza's roster record
(slot 0, +0x119, bit 0x80 — region is all-zero otherwise; the game itself zeroed it
once between the two logged writes, so 0x00 is the proven original)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ADDR = 0x1411A18D0 + 0x119

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    cur = rd(h, ADDR, 1)[0]
    print(f"+0x119 before: {cur:#04x}")
    if cur == 0x80:
        wr(h, ADDR, bytes([0x00]))
        print(f"+0x119 after:  {rd(h, ADDR, 1)[0]:#04x} (cleared)")
    elif cur == 0x00:
        print("already clear (game reset it) -- nothing to do")
    else:
        print("UNEXPECTED value -- not touching it")
finally:
    k32.CloseHandle(h)
