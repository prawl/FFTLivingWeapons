"""Dump the region around the action-menu cursor byte 0x1407FC620 (FFTHandsFree
PROPOSAL_menucursor_drift.md: the byte the game reads on Enter, 0=Move..). Show -0x40..+0x80
as bytes + u16s so we can spot the command-id list + a count near the cursor.
Usage: python menu_cursor.py [hexbase]  (default 0x1407FC620)"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

CUR = int(sys.argv[1], 16) if len(sys.argv) > 1 else 0x1407FC620

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    lo = CUR - 0x40
    b = rd(h, lo, 0xC0)
    if not b:
        sys.exit("unreadable")
    print(f"cursor byte @ {CUR:#x} = {b[0x40]}")
    for row in range(0, 0xC0, 16):
        addr = lo + row
        chunk = b[row:row + 16]
        marker = " <== cursor" if lo + row <= CUR < lo + row + 16 else ""
        print(f"{addr:#011x}: " + " ".join(f"{v:02X}" for v in chunk) + marker)
    print("\nu16 view (offset from cursor : value):")
    for off in range(-16, 48, 2):
        i = 0x40 + off
        v = b[i] | (b[i + 1] << 8)
        if v:
            print(f"  cur{off:+#05x}: {v}")
finally:
    k32.CloseHandle(h)
