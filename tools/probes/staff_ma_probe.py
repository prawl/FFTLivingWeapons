"""Dump each live band unit's weapon id + MA (total & raw) so we can see whether a caster's MA is
actually grown (GrowthEngine holds CMa = band +0x23 = round(natural*1.3) at +3) and compare the
Sanctus Staff (id 64) wielder against the Blazing Staff (id 63) wielder. Read-only.

Band-entry frame == static-array layout. Weapon id sits at +0x04 (CWeapon 0x20 - BandEntry 0x1C).
MA total +0x23, MA raw +0x27, PA total +0x22, PA raw +0x26 (the values that drive damage/spells).
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ct_probe import PROC, PV_W, find_pid, k32, rd

ANCHOR, STRIDE, ENTRY = 0x14184F890, 0x200, 0x1C
BBASE, BSLOTS = ANCHOR + ENTRY - 24 * STRIDE, 49

pid = find_pid(PROC)
if not pid:
    sys.exit(f"{PROC} not running")
h = k32.OpenProcess(PV_W, False, pid)
try:
    print(f"{'slot':>4} {'wpn':>4} {'lvl':>3} {'br':>3} {'fa':>3} {'PAtot':>5} {'PAraw':>5} "
          f"{'MAtot':>5} {'MAraw':>5} {'hp/mhp':>10} {'pos':>7}")
    for s in range(BSLOTS):
        addr = BBASE + s * STRIDE
        b = rd(h, addr, 0x60)
        if not b:
            continue
        mhp = b[0x16] | (b[0x17] << 8)
        lvl, br, fa = b[0x0D], b[0x0E], b[0x10]
        if not (1 <= mhp < 2000 and 1 <= lvl <= 99 and 1 <= br <= 100 and 1 <= fa <= 100):
            continue
        wpn = b[0x04] | (b[0x05] << 8)
        hp = b[0x14] | (b[0x15] << 8)
        tag = ""
        if wpn == 64:
            tag = "  <- Sanctus"
        elif wpn == 63:
            tag = "  <- Blazing"
        print(f"{s:>4} {wpn:>4} {lvl:>3} {br:>3} {fa:>3} {b[0x22]:>5} {b[0x26]:>5} "
              f"{b[0x23]:>5} {b[0x27]:>5} {hp:>4}/{mhp:<4} ({b[0x33]:>2},{b[0x34]:>2}){tag}")
finally:
    k32.CloseHandle(h)
