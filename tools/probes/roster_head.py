"""Hexdump roster record heads + find the Yoichi (item 90) wielder.

Roster: base 0x1411A18D0, stride 0x258, 20 slots. RHand u16 @+0x14, OffHand u16 @+0x18,
job id u8 @+0x02 (assumed), lvl/br/fa @+0x1D/1E/1F. Prints +0x00..+0x31 hex for each
live unit so we can hunt a primary-skillset byte (Ramza's Mettle rec = 27 = 0x1B).
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

ROSTER = 0x1411A18D0
STRIDE = 0x258
SLOTS = 20

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    for s in range(SLOTS):
        base = ROSTER + s * STRIDE
        b = rd(h, base, 0x32)
        if not b:
            continue
        lvl = b[0x1D]
        if not (1 <= lvl <= 99):
            continue
        rh = b[0x14] | (b[0x15] << 8)
        oh = b[0x18] | (b[0x19] << 8)
        tag = " <== YOICHI WIELDER" if 90 in (rh, oh) else ""
        print(f"slot {s:2} lvl={lvl:2} job@02={b[0x02]:3} ({b[0x02]:#04x}) rh={rh} oh={oh}{tag}")
        print("   " + " ".join(f"{v:02X}" for v in b))
finally:
    k32.CloseHandle(h)
