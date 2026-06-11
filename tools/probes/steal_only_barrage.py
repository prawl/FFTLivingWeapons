"""Monster-style single-ability command: overwrite rec 14 (Steal) so it contains ONLY
Barrage (358) at slot 9 (aligned with the DLL's own hold slot, so they agree).
All other ability slots zeroed; extend bytes = [00][80]; ExtRSM/RSM untouched.
Game restart (or barrage_probe restore 14) reverts."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, wr

ABILITY_BASE = 0x140679436 - 27 * 25
flag = ABILITY_BASE + 14 * 25 - 3
ab = ABILITY_BASE + 14 * 25

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    slots = bytearray(16)
    slots[8] = 102                          # slot 9 (0-indexed 8) = Barrage low byte
    wr(h, ab, bytes(slots))
    wr(h, flag, bytes([0x00, 0x80]))        # extend: byte1 bit7 = slot 9 only
    b = rd(h, flag, 19)
    print("rec 14 flags+abilities:", " ".join(f"{v:02X}" for v in b))
    print("Steal is now a single-ability command: Barrage.")
finally:
    k32.CloseHandle(h)
