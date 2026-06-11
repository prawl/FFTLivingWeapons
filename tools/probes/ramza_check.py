"""Read Ramza (roster slot 0): nameId @+0x230, the stray-write byte @+0x119,
and neighbors, to confirm the story-canonical test and assess the bad learned-bit write
(the buggy DLL OR'd 0x80 into +0x32 + 77*3 + 0 = +0x119)."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

ROSTER = 0x1411A18D0

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    b = rd(h, ROSTER, 0x238)
    name_id = b[0x230] | (b[0x231] << 8)
    print(f"slot0 nameId={name_id} job@02={b[0x02]} spriteSet={b[0x00]}")
    seg = b[0x110:0x128]
    print("bytes +0x110..+0x127:", " ".join(f"{v:02X}" for v in seg))
    print(f"stray byte +0x119 = {b[0x119]:#04x} (bit 0x80 {'SET' if b[0x119] & 0x80 else 'clear'})")
finally:
    k32.CloseHandle(h)
