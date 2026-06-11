"""Find Ramza's roster record + the Mettle learned-bitfield (he JUST learned slot-10
Barrage, so byte1 bit 0x40 of exactly one jobIdx triple should be set).

Roster: base 0x1411A18D0, stride 0x258, 20 slots; lvl/brave/faith at +0x1D/+0x1E/+0x1F.
Learned bitfields: +0x32 + jobIdx*3 (FFTHandsFree UNIT_DATA_STRUCTURE.md);
bytes 0-1 = action abilities MSB-first (byte1 bit6 = AbilityId10), byte 2 = passives.
currentJobJp: +0x80 u16.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd, u16

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
        b = rd(h, base, 0xB0)
        if not b:
            continue
        lvl, br, fa = b[0x1D], b[0x1E], b[0x1F]
        if not (1 <= lvl <= 99):
            continue
        tag = " <== lvl/br/fa = 99/97/75 (Ramza?)" if (lvl, br, fa) == (99, 97, 75) else ""
        print(f"slot {s:2} lvl={lvl} br={br} fa={fa} jp@0x80={u16(b, 0x80)}{tag}")
        if not tag and len(sys.argv) < 2:
            continue
        for j in range(24):
            t = b[0x32 + j * 3: 0x32 + j * 3 + 3]
            if any(t):
                mark = "  ** slot-10 bit set **" if t[1] & 0x40 else ""
                print(f"    jobIdx {j:2}: {t[0]:08b} {t[1]:08b} {t[2]:08b}{mark}")
finally:
    k32.CloseHandle(h)
