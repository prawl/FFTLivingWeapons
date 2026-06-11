import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

BASE = 0x140679436 - 27 * 25
pid = find_pid(PROC)
h = k32.OpenProcess(PV_W, False, pid)
try:
    for rec in [int(x) for x in sys.argv[1:]]:
        buf = rd(h, BASE + rec * 25, 25)
        print(f"rec {rec:3}: " + " ".join(f"{x:02X}" for x in buf))
finally:
    k32.CloseHandle(h)
