"""Set Ramza's learned bit for the injected Barrage (Archer jobIdx 3, action slot 9).
Learned triple at roster +0x32 + jobIdx*3; bytes 0-1 = action slots 1-8 / 9-16, MSB-first.
Slot 9 -> byte 1, bit 0x80. Roster slot 0 (Ramza, currently job 77 = Archer)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ADDR = 0x1411A18D0 + 0x32 + 3 * 3 + 1   # slot 0, jobIdx 3 (Archer), byte 1

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    cur = rd(h, ADDR, 1)[0]
    print(f"Archer learned byte1 before: {cur:08b}")
    wr(h, ADDR, bytes([cur | 0x80]))
    print(f"Archer learned byte1 after:  {rd(h, ADDR, 1)[0]:08b} (slot 9 = Barrage learned)")
finally:
    k32.CloseHandle(h)
