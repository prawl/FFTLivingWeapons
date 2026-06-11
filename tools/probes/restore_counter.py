"""One-shot: write the saved reaction baseline (00 00 08 00 = Counter, RSM 186) back
onto the fp=454/96 chocobo's band reaction field (band+0x78 = combat+0x94)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr
from poison_probe import locate_blocking

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    u = locate_blocking(h, 454, 96)
    off = 0x94 - 0x1C
    before = rd(h, u["band"] + off, 4)
    wr(h, u["band"] + off, bytes([0x00, 0x00, 0x08, 0x00]))
    after = rd(h, u["band"] + off, 4)
    print(f"band@{u['band']:012X}+0x{off:02X}: "
          f"{' '.join(f'{x:02X}' for x in before)} -> {' '.join(f'{x:02X}' for x in after)}")
finally:
    k32.CloseHandle(h)
