"""Refund Patrick's 1200 JP test purchase: restore Ramza's Archer JP (slot 0,
+0x86 u16, jobIdx 3 in the per-job JP array at +0x80 + jobIdx*2) to the
snapshot-proven pre-purchase value 0x1ADB (6875)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ADDR = 0x1411A18D0 + 0x86

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    b = rd(h, ADDR, 2)
    print(f"Archer JP before: {b[0] | (b[1] << 8)}")
    wr(h, ADDR, bytes([0xDB, 0x1A]))
    b = rd(h, ADDR, 2)
    print(f"Archer JP after:  {b[0] | (b[1] << 8)} (refunded)")
finally:
    k32.CloseHandle(h)
