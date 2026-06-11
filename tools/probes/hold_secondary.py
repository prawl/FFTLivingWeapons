"""Hold Ramza's +0x07 secondary at a given rec id for N seconds (the game re-derives the
byte from his stored choice on menu refreshes; the hold wins the race at battle-init).
Usage: python hold_secondary.py <recId> <seconds>"""
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

rec = int(sys.argv[1])
secs = float(sys.argv[2])
ADDR = 0x1411A18D0 + 0x07

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
flips = 0
try:
    end = time.time() + secs
    while time.time() < end:
        cur = rd(h, ADDR, 1)
        if cur and cur[0] != rec:
            wr(h, ADDR, bytes([rec]))
            flips += 1
        time.sleep(0.03)
    print(f"hold ended: re-asserted {flips} times over {secs:.0f}s; final = {rd(h, ADDR, 1)[0]}")
finally:
    k32.CloseHandle(h)
