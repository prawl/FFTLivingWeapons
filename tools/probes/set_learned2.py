"""Set a learned action bit on Ramza (roster slot 0) for any jobIdx/slot.
Usage: python set_learned2.py <jobIdx> <slot1to16>
Learned triple at +0x32 + jobIdx*3; byte0 = slots 1-8, byte1 = slots 9-16, MSB-first."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

job_idx = int(sys.argv[1])
slot1 = int(sys.argv[2])
byte_off = 0 if slot1 <= 8 else 1
mask = 1 << (7 - (slot1 - 1) % 8)
ADDR = 0x1411A18D0 + 0x32 + job_idx * 3 + byte_off

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    cur = rd(h, ADDR, 1)[0]
    print(f"jobIdx {job_idx} byte{byte_off} before: {cur:08b}")
    wr(h, ADDR, bytes([cur | mask]))
    print(f"jobIdx {job_idx} byte{byte_off} after:  {rd(h, ADDR, 1)[0]:08b} (slot {slot1} learned)")
finally:
    k32.CloseHandle(h)
