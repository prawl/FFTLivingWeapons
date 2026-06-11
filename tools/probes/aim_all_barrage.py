"""Fill ALL 16 ability slots of rec 8 (Archer Aim) with ability 358 (Barrage) and set every
extend bit (per-byte MSB: byte0=slots 1-8, byte1=slots 9-16 -> FF FF). Leaves ExtRSM and the
RSM bytes untouched. Also sets Ramza's (slot 0) learned bits for all 16 Aim action slots.
Session-only; 'barrage_probe.py restore 8' or a game restart reverts the record."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ABILITY_BASE = 0x140679436 - 27 * 25
flag = ABILITY_BASE + 8 * 25 - 3
ab = ABILITY_BASE + 8 * 25
LEARNED = 0x1411A18D0 + 0x32 + 3 * 3   # Ramza slot 0, jobIdx 3 (Archer)

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    wr(h, ab, bytes([102] * 16))           # all 16 slots = byte 102
    wr(h, flag, bytes([0xFF, 0xFF]))       # all extend bits -> every slot = 358
    wr(h, LEARNED, bytes([0xFF, 0xFF]))    # all 16 action slots learned
    b = rd(h, flag, 19)
    print("rec 8 flags+abilities:", " ".join(f"{v:02X}" for v in b))
    t = rd(h, LEARNED, 3)
    print(f"learned triple: {t[0]:08b} {t[1]:08b} {t[2]:08b}")
    print("Aim is now wall-to-wall Barrage.")
finally:
    k32.CloseHandle(h)
